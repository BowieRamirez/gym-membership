// Owner fixes 2026-11-25, Fix 3 Phase 3.2 (S6, S7): payroll pays from the later of check-in and shift start, keeps the real
// check-in time, and flags Late and Left early (flagged records are approved one by one). Records without a shift pay as before.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`ps-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin', 'Sys Admin');
const coach = await user('coach', 'Coach Hour');
const desk = await user('employee', 'Desk Ana');
const period = async start => (await as(admin, `select payroll_period($1) p`, [start]))[0].p;
const row = async (start, uid) => (await period(start)).rows.find(r => r.user_id === uid);
const records = async (pid, uid) => as(admin, `select * from payroll_records_for($1,$2) order by starts_at`, [pid, uid]);
const t = (day, hm) => `2026-09-${day}T${hm}:00+08:00`;
async function visit(day, inAt, outAt, shiftFrom, shiftTo) {
  return (await db.query(`insert into coach_visits(coach_id, checked_in_by, checked_in_at, checked_out_at, expected_start, expected_end)
    values ($1,$1,$2,$3,$4,$5) returning coach_visit_id`,
    [coach, t(day, inAt), t(day, outAt), shiftFrom && t(day, shiftFrom), shiftTo && t(day, shiftTo)])).rows[0].coach_visit_id;
}
const approve = (pid, src, id) => as(admin, `select payroll_review_time($1,$2,$3,'approved',null)`, [pid, src, id]);

try {
  await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [coach]);
  await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [desk]);
  const pid = (await period('2026-09-01')).period_id;
  let early, late, leftEarly, noShift;

  await test('S6: checking in early pays from the shift start; the real check-in time is kept', async () => {
    early = await visit('01', '07:56', '17:00', '08:00', '17:00');
    const r = (await records(pid, coach)).find(x => x.source_id == early);
    assert.equal(Number(r.hours), 9);
    assert.equal(new Date(r.starts_at).toISOString(), new Date(t('01', '08:00')).toISOString());
    assert.equal(new Date(r.actual_start).toISOString(), new Date(t('01', '07:56')).toISOString());
    assert.equal(new Date(r.expected_start).toISOString(), new Date(t('01', '08:00')).toISOString());
    assert.deepEqual(r.flags, []);
  });
  await test('late check-in pays from check-in and is flagged Late; late check-out pays to check-out', async () => {
    late = await visit('02', '08:30', '17:30', '08:00', '17:00');
    const r = (await records(pid, coach)).find(x => x.source_id == late);
    assert.equal(Number(r.hours), 9);
    assert.deepEqual(r.flags, ['late']);
  });
  await test('S7: leaving before the shift end is flagged Left early and paid for the time worked', async () => {
    leftEarly = await visit('03', '08:00', '15:00', '08:00', '17:00');
    const r = (await records(pid, coach)).find(x => x.source_id == leftEarly);
    assert.equal(Number(r.hours), 7);
    assert.deepEqual(r.flags, ['left_early']);
  });
  await test('in and out both before the shift start counts nothing (no negative time)', async () => {
    const id = await visit('04', '06:00', '07:00', '08:00', '17:00');
    const r = (await records(pid, coach)).find(x => x.source_id == id);
    assert.equal(Number(r.hours), 0);
    await approve(pid, 'coach_visit', id);
  });
  await test('a record with no shift pays from check-in as before', async () => {
    noShift = await visit('05', '09:00', '12:00', null, null);
    const r = (await records(pid, coach)).find(x => x.source_id == noShift);
    assert.equal(Number(r.hours), 3);
    assert.equal(r.expected_start, null);
  });
  await test('Approve all skips Late and Left early; the paid total uses the clipped hours', async () => {
    await as(admin, `select payroll_approve_all($1, $2)`, [pid, coach]);
    let r = await row('2026-09-01', coach);
    assert.equal(Number(r.logged), 9 + 3);   // early (clipped) + no shift; late and left early still pending
    assert.equal(r.pending, 2);
    await approve(pid, 'coach_visit', late); await approve(pid, 'coach_visit', leftEarly);
    r = await row('2026-09-01', coach);
    assert.equal(Number(r.logged), 9 + 3 + 9 + 7);
  });
  await test('front desk cash shifts follow the same rule', async () => {
    const id = (await db.query(`insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted,
      expected_start, expected_end) values ($1,$2,$3,0,0,0,0,0,$4,$5) returning shift_id`,
      [desk, t('01', '07:57'), t('01', '13:00'), t('01', '08:00'), t('01', '14:00')])).rows[0].shift_id;
    const r = (await records(pid, desk)).find(x => x.source_id == id);
    assert.equal(Number(r.hours), 5);
    assert.deepEqual(r.flags, ['left_early']);
  });
} finally {
  console.log(`Payroll schedule: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
