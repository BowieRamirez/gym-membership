// Owner fixes 2026-11-25, Fix 4 Phase 4.1 (W2, W6): Attendance Review reads every person's records and hour corrections for a
// period in one call, each record with whose it is (name, role) and how they are paid (pay type, rate).
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
async function asAnon(sql, args = []) {
  await db.query('begin');
  try { await db.query('set local role anon'); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`pra-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin', 'Sys Admin');
const coach = await user('coach', 'Coach Hour');
const desk = await user('employee', 'Juan Dela Cruz');
const member = await user('member', 'Member Ben');
const t = (day, hm) => `2026-10-${day}T${hm}:00+08:00`;
const all = async pid => (await as(admin, `select payroll_records_all($1) j`, [pid]))[0].j;

try {
  await as(admin, `select set_staff_salary($1, 130, 'hourly')`, [coach]);
  await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [desk]);
  const pid = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p.period_id;
  const visit = (await db.query(`insert into coach_visits(coach_id, checked_in_by, checked_in_at, checked_out_at, expected_start, expected_end)
    values ($1,$1,$2,$3,$4,$5) returning coach_visit_id`, [coach, t('01', '08:10'), t('01', '12:00'), t('01', '08:00'), t('01', '12:00')])).rows[0].coach_visit_id;
  const shift = (await db.query(`insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted)
    values ($1,$2,$3,0,0,0,0,0) returning shift_id`, [desk, t('02', '08:00'), t('02', '17:00')])).rows[0].shift_id;
  await as(admin, `select * from payroll_add_hours($1,$2,0.5,'Forgot to close the drawer')`, [pid, desk]);
  await as(admin, `select * from payroll_add_hours($1,$2,-1,'Long lunch')`, [pid, coach]);

  await test('one call lists every person\'s records with name, role, pay type and rate', async () => {
    const j = await all(pid);
    const v = j.records.find(r => r.source === 'coach_visit' && r.source_id == visit);
    const s = j.records.find(r => r.source === 'cash_shift' && r.source_id == shift);
    assert.ok(v && s, 'both people listed');
    assert.equal(v.user_id, coach); assert.equal(v.full_name, 'Coach Hour'); assert.equal(v.role_name, 'coach');
    assert.equal(v.pay_type, 'hourly'); assert.equal(Number(v.rate), 130);
    assert.equal(Number(v.hours), 3.83); assert.deepEqual(v.flags, ['late']); assert.equal(v.status, 'pending');
    assert.ok(v.actual_start && v.expected_start && v.expected_end && v.ends_at && v.starts_at && v.label);
    assert.equal(s.full_name, 'Juan Dela Cruz'); assert.equal(s.role_name, 'employee'); assert.equal(Number(s.rate), 100);
    assert.equal(Number(s.hours), 9); assert.equal(s.expected_start, null);
    assert.ok(!j.records.some(r => r.user_id === member), 'no member');
  });
  await test('the period\'s hour corrections for everyone, with whose they are', async () => {
    const j = await all(pid);
    const d = j.adjustments.find(a => a.user_id === desk), c = j.adjustments.find(a => a.user_id === coach);
    assert.equal(Number(d.quantity), 0.5); assert.equal(d.note, 'Forgot to close the drawer'); assert.equal(d.full_name, 'Juan Dela Cruz');
    assert.equal(d.created_by_name, 'Sys Admin'); assert.ok(d.adjustment_id && d.created_at);
    assert.equal(Number(c.quantity), -1);
  });
  await test('an approval shows on the next read', async () => {
    await as(admin, `select payroll_review_time($1,'coach_visit',$2,'approved',null)`, [pid, visit]);
    assert.equal((await all(pid)).records.find(r => r.source_id == visit && r.source === 'coach_visit').status, 'approved');
  });
  await test('front desk, coach, member and anon are refused', async () => {
    for (const who of [desk, coach, member]) await assert.rejects(as(who, `select payroll_records_all($1)`, [pid]), /forbidden/);
    await assert.rejects(asAnon(`select payroll_records_all(1)`), /permission denied|forbidden/);
  });
} finally {
  console.log(`Payroll records all: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
