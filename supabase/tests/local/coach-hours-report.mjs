// Phase 14 (E11): Coach hours report for admins; counts completed sessions and hours only; no-shows & coach cancellations apart.
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
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`ch-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const admin = await user('admin', 'Sys Admin');
const sub = await user('subadmin', 'Sub Admin');
const desk = await user('employee', 'Desk Dana');
const coach = await user('coach', 'Coach Kai');
const member = await user('member', 'Mia Santos');

const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date::text d`)).rows[0].d;
const at = (d, h) => new Date(`${d}T${String(h).padStart(2, '0')}:00:00+08:00`).toISOString();

// 1 completed 2h, 1 no-show 1h, 1 cancelled by coach 1h, 1 cancelled by member 1h, 1 scheduled 1h
await db.query(
  `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
   values
     ($1,$2,$3,$4,'completed'),
     ($1,$2,$5,$6,'no_show'),
     ($1,$2,$7,$8,'scheduled')`,
  [coach, member, at(today, 8), at(today, 10), at(today, 10), at(today, 11), at(today, 11), at(today, 12)]
);
await db.query(
  `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status, cancelled_by)
   values
     ($1,$2,$3,$4,'cancelled',$1),
     ($1,$2,$5,$6,'cancelled',$2)`,
  [coach, member, at(today, 13), at(today, 14), at(today, 14), at(today, 15)]
);

try {
  await test('front desk cannot call coach_hours_report; admins and subadmins can', async () => {
    await assert.rejects(as(desk, 'select * from coach_hours_report($1,$1)', [today]), /forbidden/);
    await assert.rejects(as(coach, 'select * from coach_hours_report($1,$1)', [today]), /forbidden/);
    const rowsA = await as(admin, 'select * from coach_hours_report($1,$1)', [today]);
    const rowsS = await as(sub, 'select * from coach_hours_report($1,$1)', [today]);
    assert.equal(rowsA.length, 1);
    assert.equal(rowsS.length, 1);
  });

  await test('counts completed sessions and hours only; no-shows and coach cancellations counted apart', async () => {
    const row = (await as(admin, 'select * from coach_hours_report($1,$1)', [today]))[0];
    assert.equal(row.coach_name, 'Coach Kai');
    assert.equal(row.completed_sessions, 1);
    assert.equal(Number(row.completed_hours), 2);
    assert.equal(row.no_shows, 1);
    assert.equal(row.coach_cancelled, 1);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nCoachHoursReport: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
