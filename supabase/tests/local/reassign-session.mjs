// Owner fixes 2026-11-25, Fix 3 Phase 3.5 (S9): when a coach doesn't come, the desk or an admin moves a booked session to another coach
// who is free then (a shift covering it, nothing else booked, under the daily hour limit). Time, price and payment stay; everyone is told.
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
    [`rs-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const one = async (sql, args = []) => (await db.query(sql, args)).rows[0];
const desk = await user('employee', 'Desk Ana');
const absent = await user('coach', 'Coach Away');
const cover = await user('coach', 'Coach Cover');
const busy = await user('coach', 'Coach Busy');
const noShift = await user('coach', 'Coach Off');
const member = await user('member', 'Member Ben');
const day = (await one(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).d;
const at = t => `${day} ${t}:00+08`;
const shift = (c, from, to) => db.query(`insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)`, [c, at(from), at(to)]);
const session = async (c, from, to, who = member) => (await one(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end)
  values($1,$2,$3,$4) returning training_session_id id`, [c, who, at(from), at(to)])).id;
await db.query(`update gym_settings set opens_at='06:00', closes_at='22:00', max_coach_hours_per_day=6`);
for (const c of [absent, cover, busy]) await shift(c, '08:00', '18:00');
const s = await session(absent, '10:00', '11:00');

try {
  await test('the desk moves a session to a free coach; time and member stay; the move is told and audited', async () => {
    await as(desk, 'select reassign_session($1, $2, $3)', [s, cover, 'Coach Away is sick']);
    const row = await one('select * from training_sessions where training_session_id=$1', [s]);
    assert.equal(row.coach_id, cover);
    assert.equal(row.member_id, member);
    assert.equal(new Date(row.scheduled_start).toISOString(), new Date(at('10:00')).toISOString());
    for (const who of [member, absent, cover])
      assert.ok((await one('select count(*)::int n from user_notifications where user_id=$1', [who])).n >= 1);
    assert.equal((await one(`select count(*)::int n from audit_log where action='session.reassigned' and entity_id=$1`, [String(s)])).n, 1);
  });
  await test('a coach with no shift then, already booked then, or over the day\'s hours is refused', async () => {
    const s2 = await session(cover, '12:00', '13:00');
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [s2, noShift, 'x']), /coach_not_free/);
    await session(busy, '12:00', '13:00', await user('member', 'Other Member'));
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [s2, busy, 'x']), /coach_not_free/);
    await session(absent, '15:00', '16:00', await user('member', 'Third Member'));   // absent already works 1 hour that day
    await db.query(`update gym_settings set max_coach_hours_per_day=1`);
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [s2, absent, 'x']), /coach_day_full/);
    await db.query(`update gym_settings set max_coach_hours_per_day=6`);
  });
  await test('a started, cancelled or same-coach session is refused, and a reason is required', async () => {
    const past = (await one(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end,status) values($1,$2,now()-interval '1 hour',now(),'completed') returning training_session_id id`, [cover, member])).id;
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [past, absent, 'x']), /session_started/);
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [s, cover, 'x']), /bad_input/);
    await assert.rejects(as(desk, 'select reassign_session($1, $2, $3)', [s, absent, '  ']), /bad_reason/);
  });
  await test('members, coaches and anon cannot move a session', async () => {
    for (const who of [member, cover]) await assert.rejects(as(who, 'select reassign_session($1, $2, $3)', [s, absent, 'x']), /forbidden/);
    await assert.rejects(asAnon('select reassign_session($1, $2, $3)', [s, absent, 'x']), /permission denied|42501/);
  });
} finally {
  console.log(`Reassign session: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
