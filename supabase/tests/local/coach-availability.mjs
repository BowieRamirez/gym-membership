// Phase 5 (D6): gym hours in Gym settings; a coach's posted shifts are their opened availability. Shifts sit on the
// 30-minute grid inside gym hours on one Manila day, and cannot be cancelled, moved or deleted out from under a scheduled session.
// Amenity passes: buying makes a pending pass that grants nothing until staff verify the payment.
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
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p5-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), member = await user();
// a day well ahead, as Manila wall-clock times
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const at = hm => `${day} ${hm}:00+08`;
const post = (from, to, who = coach) => as(who, `insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3) returning *`, [coach, at(from), at(to)]);
const session = async (from, to) => (await db.query(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end)
  values($1,$2,$3,$4) returning training_session_id`, [coach, member, at(from), at(to)])).rows[0].training_session_id;

try {
  await test('gym hours default to 06:00-22:00 and only settings managers change them', async () => {
    const g = (await as(member, 'select opens_at::text, closes_at::text from gym_settings'))[0];
    assert.deepEqual(g, { opens_at: '06:00:00', closes_at: '22:00:00' });
    await as(desk, `update gym_settings set opens_at='05:00'`);   // no rows for desk: RLS filters it out
    assert.equal((await db.query('select opens_at::text o from gym_settings')).rows[0].o, '06:00:00');
    await assert.rejects(as(admin, `update gym_settings set opens_at='22:00', closes_at='06:00'`), /check|bad_hours/);
    await assert.rejects(as(admin, `update gym_settings set opens_at='06:15'`), /check|bad_hours/);
  });
  await test('shifts must sit on the half-hour grid inside gym hours on one day', async () => {
    await assert.rejects(post('08:15', '10:00'), /off_grid/);
    await assert.rejects(post('05:30', '07:00'), /outside_gym_hours/);
    await assert.rejects(post('21:00', '22:30'), /outside_gym_hours/);
    await assert.rejects(as(coach, `insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)`, [coach, at('21:00'), `${day} 23:00:00+08`]), /outside_gym_hours/);
    const s = (await post('06:00', '22:00'))[0];
    assert.ok(s.shift_post_id);
    await as(coach, `update shift_posts set status='cancelled' where shift_post_id=$1`, [s.shift_post_id]);
  });
  await test('nobody else can open a coach\'s availability', async () => {
    await assert.rejects(post('08:00', '09:00', member), /row-level security|42501/);
    await assert.rejects(post('08:00', '09:00', desk), /row-level security|42501/);
  });
  await test('a shift with a scheduled session cannot be cancelled, deleted or shrunk away from it', async () => {
    const s = (await post('08:00', '12:00'))[0];
    await session('10:00', '11:00');
    await assert.rejects(as(coach, `update shift_posts set status='cancelled' where shift_post_id=$1`, [s.shift_post_id]), /shift_has_sessions/);
    await assert.rejects(as(coach, `delete from shift_posts where shift_post_id=$1`, [s.shift_post_id]), /shift_has_sessions/);
    await assert.rejects(as(coach, `update shift_posts set ends_at=$2 where shift_post_id=$1`, [s.shift_post_id, at('10:30')]), /shift_has_sessions/);
    await as(coach, `update shift_posts set starts_at=$2, note='Later start' where shift_post_id=$1`, [s.shift_post_id, at('09:00')]);   // still covers it
    assert.equal((await db.query('select status from shift_posts where shift_post_id=$1', [s.shift_post_id])).rows[0].status, 'scheduled');
  });
  await test('an empty shift can be cancelled and a cancelled session does not block', async () => {
    const s = (await post('13:00', '15:00'))[0];
    const id = await session('13:30', '14:00');
    await db.query(`update training_sessions set status='cancelled' where training_session_id=$1`, [id]);
    await as(coach, `update shift_posts set status='cancelled' where shift_post_id=$1`, [s.shift_post_id]);
  });
  await test('a bought amenity pass grants nothing until staff verify the payment', async () => {
    const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P5 Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
    const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    const court = (await db.query(`insert into amenities(name,price,duration_days) values('P5 Court',300,30) returning amenity_id`)).rows[0].amenity_id;
    const bought = (await as(member, 'select * from avail_amenity($1)', [court]))[0];
    const pass = async () => (await as(member, 'select status, expires_at from user_amenities where amenity_id=$1', [court]))[0];
    assert.equal((await pass()).status, 'pending');
    await as(desk, 'select verify_payment($1,true)', [bought.payment_id]);
    const p = await pass(); assert.equal(p.status, 'active'); assert.ok(p.expires_at > new Date());
  });
  // Phase 9 (0033)
  await test('a shift cannot start in the past or overlap another of the same coach', async () => {
    const yesterday = (await db.query(`select ((now() at time zone 'Asia/Manila')::date - 1)::text d`)).rows[0].d;
    await assert.rejects(as(coach, `insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)`, [coach, `${yesterday} 08:00:00+08`, `${yesterday} 10:00:00+08`]), /past_slot/);
    await post('16:00', '18:00');
    await assert.rejects(post('17:00', '19:00'), /shift_posts_no_overlap/);
  });
  await test('an hourly rate must be above zero; empty stays allowed', async () => {
    await assert.rejects(as(coach, `update coaches set hourly_rate=0 where user_id=$1`, [coach]), /coaches_rate_positive/);
    await assert.rejects(as(coach, `update coaches set hourly_rate=-5 where user_id=$1`, [coach]), /check/);
    await as(coach, `update coaches set hourly_rate=30.5 where user_id=$1`, [coach]);
    assert.equal((await db.query('select hourly_rate::text r from coaches where user_id=$1', [coach])).rows[0].r, '30.50');
    await as(coach, `update coaches set hourly_rate=null where user_id=$1`, [coach]);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nCoach availability: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
