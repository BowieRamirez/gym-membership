// Item 7 (0043): an amenity sold per session uses up one session per booking, and amenities are used only while a coach is in the gym.
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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`s-${++serial}@test.io`, JSON.stringify({ full_name: 'Session Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee'), coach = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('S Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = async (name, days) => (await db.query('insert into amenities(name,price,duration_days) values($1,150,$2) returning amenity_id id', [name, days])).rows[0].id;
const pay = p => as(desk, 'select verify_payment($1,true)', [p.payment_id]);
async function member() {
  const uid = await user();
  await pay((await as(uid, 'select * from extend_membership($1,null)', [pack]))[0]);
  return uid;
}
const buy = async (uid, a) => { const p = (await as(uid, 'select * from avail_amenity($1)', [a]))[0]; await pay(p); return p; };
const passOf = async (uid, a) => (await db.query('select * from user_amenities where member_id=$1 and amenity_id=$2 order by user_amenity_id desc limit 1', [uid, a])).rows[0];
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const book = (uid, list) => as(uid, 'select * from book_slots($1::jsonb)', [JSON.stringify(list.map(([a, hm]) => ({ amenity: a, start: at(hm) })))]);
const coachIn = () => db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
const coachOut = () => db.query('update coach_visits set checked_out_at=now(),checked_out_by=coach_id where checked_out_at is null');

try {
  await test('an amenity with no duration sells one session; one with a duration still sells a timed pass', async () => {
    const uid = await member(), session = await amenity('S Session 1', null), timed = await amenity('S Timed 1', 30);
    await buy(uid, session); await buy(uid, timed);
    const s = await passOf(uid, session), t = await passOf(uid, timed);
    assert.deepEqual([s.is_session, s.status, s.expires_at], [true, 'active', null]);
    assert.equal(t.is_session, false); assert.ok(t.expires_at);
  });
  await test('a counter payment for a per-session amenity is a session too', async () => {
    const uid = await member(), session = await amenity('S Session 2', null);
    await as(desk, `select * from record_counter_payment('amenity','cash',$1,$2)`, [uid, session]);
    assert.equal((await passOf(uid, session)).is_session, true);
  });
  await test('booking uses the session up, touching slots count as one, and a second booking needs another', async () => {
    const uid = await member(), a = await amenity('S Session 3', null);
    await buy(uid, a);
    const [r] = await book(uid, [[a, '10:00'], [a, '10:30']]);
    const p = await passOf(uid, a);
    assert.equal(p.status, 'expired'); assert.equal(r.user_amenity_id, p.user_amenity_id);
    await assert.rejects(book(uid, [[a, '14:00']]), /no_pass/);
  });
  await test('two separate bookings on one session are all or nothing: neither is made and the session stays', async () => {
    const uid = await member(), a = await amenity('S Session 4', null);
    await buy(uid, a);
    await assert.rejects(book(uid, [[a, '10:00'], [a, '14:00']]), /no_pass/);
    assert.equal((await passOf(uid, a)).status, 'active');
    assert.equal((await db.query('select count(*)::int n from reservations where member_id=$1', [uid])).rows[0].n, 0);
  });
  await test('cancelling before the start gives the session back, and it can be bought again once used', async () => {
    const uid = await member(), a = await amenity('S Session 5', null);
    await buy(uid, a);
    const [r] = await book(uid, [[a, '10:00']]);
    await as(uid, `select cancel_reservation($1,'')`, [r.reservation_id]);
    assert.equal((await passOf(uid, a)).status, 'active');
    await assert.rejects(as(uid, 'select * from avail_amenity($1)', [a]), /already_availed/);
    await book(uid, [[a, '11:00']]);
    await buy(uid, a);
    assert.equal((await passOf(uid, a)).status, 'active');
  });
  await test('a timed pass already sold keeps working after the amenity switches to per session, and is used first', async () => {
    const uid = await member(), a = await amenity('S Switch', 30);
    await buy(uid, a);
    const before = await passOf(uid, a);
    await db.query('update amenities set duration_days=null,price=99 where amenity_id=$1', [a]);
    await book(uid, [[a, '10:00']]); await book(uid, [[a, '14:00']]);
    const after = await passOf(uid, a);
    assert.equal(after.user_amenity_id, before.user_amenity_id);
    assert.deepEqual([after.status, after.is_session], ['active', false]);
  });
  await test('a booking far ahead needs no coach yet', async () => {
    await coachOut();
    const uid = await member(), a = await amenity('S Ahead', 30);
    await buy(uid, a);
    assert.equal((await book(uid, [[a, '10:00']])).length, 1);
  });
  await test('a booking starting in the next slot needs a coach in the gym now', async () => {
    const hour = (await db.query(`select extract(hour from now() at time zone 'Asia/Manila')::int h`)).rows[0].h;
    if (hour >= 22) return;   // the next slot would run past the day's closing time
    await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30' where id=1`);
    const uid = await member(), a = await amenity('S Next', 30);
    await buy(uid, a);
    const next = new Date(Math.ceil((Date.now() + 1000) / 1800000) * 1800000).toISOString();
    const go = () => as(uid, 'select * from book_slots($1::jsonb)', [JSON.stringify([{ amenity: a, start: next }])]);
    await coachOut();
    await assert.rejects(go(), /no_coach_present/);
    await coachIn();
    assert.equal((await go()).length, 1);
    await coachOut();
  });
  await test('using an amenity needs a coach in the gym; an archived coach does not count', async () => {
    await coachOut();
    const uid = await member(), a = await amenity('S Use', 30);
    await buy(uid, a);
    const p = await passOf(uid, a);
    const use = () => as(uid, 'insert into amenity_usages(user_amenity_id) values($1) returning status', [p.user_amenity_id]);
    await assert.rejects(use(), /no_coach_present/);
    await coachIn();
    assert.equal((await use())[0].status, 'pending');
    await db.query('update profiles set is_active=false where id=$1', [coach]);
    await assert.rejects(use(), /no_coach_present/);
    await db.query('update profiles set is_active=true where id=$1', [coach]);
    await coachOut();
  });
  await test('nobody can call the new helpers or set is_session directly', async () => {
    const uid = await member();
    for (const fn of ['coach_in_gym()', 'guard_amenity_use()', 'mark_amenity_session()'])
      await assert.rejects(as(uid, `select public.${fn}`), /permission denied|trigger functions/);
    await assert.rejects(as(uid, 'update user_amenities set is_session=true where member_id=$1', [uid]), /permission denied/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Amenity sessions: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
