// 0057 (owner, 2026-11-11): the 7-day note reached nobody who already had less than a week left, because the nightly job only
// matched the day exactly 7 days before the end. Now it catches up, says how long is really left, and stays the member's alone.
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
  [`exp-${++serial}@test.io`, JSON.stringify({ full_name: 'Expiry Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee'), coach = await user('coach');
await db.query(`insert into cash_shifts(cashier_id, opening_float) values ($1, 5000)`, [desk]);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('Exp Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
// a member whose running term ends `days` Manila days from today, at noon
async function member(days) {
  const uid = await user();
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  await db.query(`update membership_terms set ends_at = (date_trunc('day', now() at time zone 'Asia/Manila') + make_interval(days => $2) + interval '12 hours')
    at time zone 'Asia/Manila' where member_id=$1`, [uid, days]);
  return uid;
}
const run = () => db.query('select send_expiry_reminders()');
const notes = async uid => (await db.query('select body from announcements where recipient_id=$1', [uid])).rows.map(r => r.body);
const bells = async uid => (await db.query(`select title from user_notifications where user_id=$1 and type='membership' order by notification_id`, [uid])).rows.map(r => r.title);
const NOTE = /^Your membership ends in 2 days, on \w{3} \d{1,2} \w{3} \d{4}\. Renew from Packages/;

try {
  const late = await member(2), week = await member(7), eight = await member(8), tomorrow = await member(1), renewed = await member(5);
  const next = (await as(renewed, 'select * from extend_membership($1,null)', [pack]))[0];   // a renewal already queued after the term
  await as(desk, 'select verify_payment($1,true)', [next.payment_id]);
  await run(); await run();

  await test('a member with less than a week left still gets the note, saying how long is really left, once', async () => {
    const n = await notes(late);
    assert.equal(n.length, 1); assert.match(n[0], NOTE);
  });
  await test('they get one alert for it, not a second one for the 3-day reminder', async () =>
    assert.deepEqual(await bells(late), ['Your membership ends in 2 days']));
  await test('exactly 7 days still reads "1 week"; 1 day reads "tomorrow"', async () => {
    assert.match((await notes(week))[0], /^Your membership ends in 1 week, on /);
    assert.deepEqual(await bells(week), ['Your membership ends in 1 week']);
    assert.match((await notes(tomorrow))[0], /^Your membership ends tomorrow, on /);
  });
  await test('more than a week left, or a renewal already queued: nothing', async () => {
    assert.deepEqual([await notes(eight), await bells(eight)], [[], []]);
    assert.deepEqual([await notes(renewed), await bells(renewed)], [[], []]);
  });
  await test('a member who had the 1-week note still gets the 3-day alert when it comes', async () => {
    await db.query(`update membership_terms set ends_at = ends_at - interval '4 days' where member_id=$1`, [week]);
    await run();
    assert.deepEqual(await bells(week), ['Your membership ends in 1 week', 'Your membership ends in 3 days']);
    assert.equal((await notes(week)).length, 1);
  });

  // privacy: the note is the member's alone
  await test('nobody else reads the note: other members, coaches, the desk, Admin and System admin', async () => {
    const other = await member(20);
    for (const who of [other, coach, desk, sub, admin]) {
      const own = await as(who, `select * from chat_messages('announcements',$1)`, [who]).catch(() => []);
      assert.ok(!own.some(m => NOTE.test(m.body)), 'seen in their own Announcements');
      await assert.rejects(as(who, `select * from chat_messages('announcements',$1)`, [late]), /forbidden/);
      await assert.rejects(as(who, 'select * from announcements'), /permission denied/);
      await assert.rejects(as(who, 'select * from my_announcements()'), /permission denied/);
    }
    for (const who of [sub, admin]) assert.ok(!(await as(who, 'select * from announcements_list()')).some(a => NOTE.test(a.body)), 'on the staff page');
    assert.equal((await as(other, `select * from user_notifications where user_id=$1`, [late])).length, 0);
    assert.ok((await as(late, `select * from chat_messages('announcements',$1)`, [late])).some(m => NOTE.test(m.body) && m.sender_name === 'For you'));
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exitCode = failed ? 1 : 0;
}
