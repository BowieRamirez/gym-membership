// 0140 (live system test, 2026-11-23): profiles show names, not emails, to members and coaches; only the desk checks coaches in;
// damage needs a session that started and was not cancelled; a member cancels a pending coach access payment; walk-in names <= 100.
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
const user = async (role, name) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`lt-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
await db.query(`update gym_settings set opens_at='00:00', closes_at='24:00'`);   // check-ins at any hour the test runs
const admin = await user('admin', 'Sys Admin'), desk = await user('employee', 'Desk Dana'), coach = await user('coach', 'Coach Kai');
const member = await user('member', 'Mia Member'), other = await user('member', 'Otto Other');
await db.query(`update profiles set phone='09171234567' where id=$1`, [coach]);
const session = async (start, status = 'scheduled') => (await db.query(
  `insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end,status) values($1,$2,$3::timestamptz,$3::timestamptz+interval '1 hour',$4) returning training_session_id id`,
  [coach, member, start, status])).rows[0].id;

try {
  await test('1: a member sees the coach by name in public_profiles, but not the email or phone in profiles', async () => {
    const pub = await as(member, 'select * from public_profiles where id=$1', [coach]);
    assert.equal(pub.length, 1); assert.equal(pub[0].full_name, 'Coach Kai');
    assert.deepEqual(Object.keys(pub[0]).sort(), ['avatar_path', 'first_name', 'full_name', 'id', 'is_active', 'last_name']);
    assert.equal((await as(member, 'select * from profiles where id=$1', [coach])).length, 0);
    assert.equal((await as(member, 'select * from profiles where id=$1', [member])).length, 1, 'own row');
  });
  await test('1: a member does not see another member at all; a coach sees their trainee by name only', async () => {
    assert.equal((await as(member, 'select * from public_profiles where id=$1', [other])).length, 0);
    await session(new Date(Date.now() - 864e5).toISOString(), 'completed');
    assert.equal((await as(coach, 'select * from public_profiles where id=$1', [member])).length, 1);
    assert.equal((await as(coach, 'select * from profiles where id=$1', [member])).length, 0);
  });
  await test('1: staff still read full profiles (email and phone)', async () => {
    const rows = await as(desk, 'select username, phone from profiles where id=$1', [coach]);
    assert.equal(rows[0].phone, '09171234567'); assert.match(rows[0].username, /@/);
  });
  await test('1: anonymous cannot read public_profiles', async () => {
    await db.query('begin'); await db.query('set local role anon');
    try { await assert.rejects(db.query('select * from public_profiles'), /permission denied/); } finally { await db.query('rollback'); }
  });
  await test('2: a coach cannot check themselves in or out; the desk can', async () => {
    await assert.rejects(as(coach, 'select coach_check_in()'), /forbidden/);
    await assert.rejects(as(coach, 'select coach_check_in($1)', [coach]), /forbidden/);
    assert.ok((await as(desk, 'select coach_check_in($1) id', [coach]))[0].id);
    await assert.rejects(as(coach, 'select coach_check_out()'), /forbidden/);
    assert.equal((await as(desk, 'select coach_check_out($1) ok', [coach]))[0].ok, true);
    await assert.rejects(as(member, 'select coach_check_in($1)', [coach]), /forbidden/);
  });
  await test('3: damage on a session that has not started or was cancelled is refused; a started one works', async () => {
    const future = await session(new Date(Date.now() + 864e5).toISOString());
    await assert.rejects(as(coach, `select report_damage(p_session_id=>$1, p_description=>'x')`, [future]), /session_not_started/);
    const cancelled = await session(new Date(Date.now() - 2 * 864e5).toISOString(), 'cancelled');
    await assert.rejects(as(coach, `select report_damage(p_session_id=>$1, p_description=>'x')`, [cancelled]), /session_cancelled/);
    const done = await session(new Date(Date.now() - 3 * 864e5).toISOString(), 'completed');
    assert.ok((await as(coach, `select report_damage(p_session_id=>$1, p_description=>'torn mat') id`, [done]))[0].id);
  });
  await test('4: a member cancels their own pending coach access payment; not someone else\'s', async () => {
    const pay = (await db.query(`insert into payments(payment_code,member_id,kind,method,status,amount,expires_at) values(generate_payment_code(),$1,'coach_addon','qr','pending',200,now()+interval '1 day') returning payment_id id`, [member])).rows[0].id;
    await assert.rejects(as(other, 'select cancel_pending_payment($1)', [pay]), /not_found/);
    await as(member, 'select cancel_pending_payment($1)', [pay]);
    assert.equal((await db.query('select status from payments where payment_id=$1', [pay])).rows[0].status, 'rejected');
  });
  await test('5: a walk-in name over 100 characters is refused; 100 is fine', async () => {
    await db.query(`insert into cash_shifts(cashier_id, opening_float) values($1, 5000)`, [desk]);
    await assert.rejects(as(desk, `select record_counter_payment(p_kind=>'walk_in', p_method=>'cash', p_walk_in_name=>$1)`, ['x'.repeat(101)]), /walk_in_name_length|23514|check/);
    assert.ok((await as(desk, `select (record_counter_payment(p_kind=>'walk_in', p_method=>'cash', p_walk_in_name=>$1)).payment_id id`, ['y'.repeat(100)]))[0].id);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Live test fixes: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
