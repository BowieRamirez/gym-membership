// Item 10 and the forgot-phone fix (0044): how a check-in was made is recorded, the desk can save who has paid, and a queued offline
// check-in is rechecked by the server against the day it happened and can't be applied twice.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

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
const user = async (role = 'member', name = 'Desk Person') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`c-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee'), coach = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('C Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
async function paid(name) {
  const uid = await user('member', name);
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  await as(uid, `select set_desk_pin('1234')`);   // F8 (0049): by name and offline need the PIN
  return uid;
}
const hoursAgo = h => new Date(Date.now() - h * 3600000).toISOString();
const queued = (uid, at, ref = randomUUID(), actor = desk) => as(actor, `select record_check_in($1,'offline',$2,$3,'1234') id`, [uid, at, ref]);
const rows = async uid => (await db.query('select * from gym_check_ins where member_id=$1 order by check_in_id', [uid])).rows;

try {
  await test('no method means QR (F6): once this desk has scanned a pass, it records qr', async () => {
    const uid = await paid();
    await as(desk, 'select scan_member($1)', [(await as(uid, 'select code from issue_member_pass()'))[0].code]);
    await as(desk, 'select record_check_in($1)', [uid]);
    const [r] = await rows(uid);
    assert.deepEqual([r.method, r.recorded_by, r.client_ref], ['qr', desk, null]);
  });
  await test('forgot phone: a manual check-in records how and who, and is refused without a membership', async () => {
    const uid = await paid(), none = await user();
    await as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'1234')`, [uid]);
    const [r] = await rows(uid);
    assert.deepEqual([r.method, r.recorded_by], ['manual', desk]);
    await assert.rejects(as(desk, `select record_check_in($1,'manual')`, [none]), /no_active_membership/);
    await assert.rejects(as(desk, `select record_check_in($1,'phone')`, [uid]), /bad_input/);
  });
  await test('only staff can check someone in, by any method', async () => {
    const uid = await paid();
    for (const actor of [uid, coach]) await assert.rejects(as(actor, `select record_check_in($1,'manual')`, [uid]), /forbidden|permission/);
    await assert.rejects(queued(uid, hoursAgo(1), randomUUID(), uid), /forbidden|permission/);
  });
  await test('a queued check-in keeps the time it happened and is flagged as offline', async () => {
    const uid = await paid(), at = hoursAgo(2);
    await db.query("update membership_terms set starts_at=now()-interval '1 day' where member_id=$1", [uid]);
    await queued(uid, at);
    const [r] = await rows(uid);
    assert.equal(r.method, 'offline'); assert.equal(r.checked_in_at.toISOString(), at);
  });
  await test('sending the queue twice checks in once', async () => {
    const uid = await paid(), ref = randomUUID();
    await db.query("update membership_terms set starts_at=now()-interval '1 day' where member_id=$1", [uid]);
    const first = (await queued(uid, hoursAgo(1), ref))[0].id;
    assert.equal((await queued(uid, hoursAgo(1), ref))[0].id, first);
    assert.equal((await rows(uid)).length, 1);
  });
  await test('the server rechecks against the day it happened: expired before it, or cancelled since, is refused', async () => {
    const lapsed = await user(), cancelled = await paid();
    await assert.rejects(queued(lapsed, hoursAgo(1)), /no_active_membership/);
    await db.query('update membership_terms set cancelled_at=now() where member_id=$1', [cancelled]);
    await assert.rejects(queued(cancelled, hoursAgo(1)), /no_active_membership/);
    assert.equal((await rows(cancelled)).length, 0);
  });
  await test('a term that ended after the visit still covers that visit', async () => {
    const uid = await paid();
    await db.query(`update membership_terms set starts_at=now()-interval '31 days', ends_at=now()-interval '1 hour' where member_id=$1`, [uid]);
    await queued(uid, hoursAgo(3));
    assert.equal((await rows(uid)).length, 1);
    await assert.rejects(queued(uid, new Date().toISOString()), /no_active_membership/);
  });
  await test('an archived account is refused even for a visit before it was archived', async () => {
    const uid = await paid();
    await db.query('update profiles set is_active=false where id=$1', [uid]);
    await assert.rejects(queued(uid, hoursAgo(1)), /no_active_membership/);
  });
  await test('a chosen time is only for offline, and only within limits', async () => {
    const uid = await paid();
    await assert.rejects(as(desk, `select record_check_in($1,'manual',$2)`, [uid, hoursAgo(1)]), /bad_input/);
    await assert.rejects(as(desk, 'select record_check_in($1,null,$2)', [uid, hoursAgo(1)]), /bad_input/);
    await assert.rejects(queued(uid, hoursAgo(49)), /bad_input/);
    await assert.rejects(queued(uid, new Date(Date.now() + 3600000).toISOString()), /bad_input/);
    assert.equal((await rows(uid)).length, 0);
  });
  await test('hostile text in a method is refused', async () => {
    const uid = await paid();
    await assert.rejects(as(desk, 'select record_check_in($1,$2)', [uid, `x'; drop table profiles; --`]), /bad_input/);
    assert.ok((await db.query(`select to_regclass('public.profiles') as t`)).rows[0].t);
  });
  await test('the saved list holds active accounts with their name and running or later terms (no codes, F6), for staff only', async () => {
    const uid = await paid('Listed Person'), gone = await paid('Gone Person');
    await db.query('update profiles set is_active=false where id=$1', [gone]);
    const list = (await as(desk, 'select desk_member_snapshot() s'))[0].s;
    const me = list.find(r => r.member === uid);
    assert.equal(me.name, 'Listed Person'); assert.equal(me.code, undefined); assert.equal(me.terms.length, 1);
    assert.ok(!list.some(r => r.member === gone));
    for (const actor of [uid, coach]) await assert.rejects(as(actor, 'select desk_member_snapshot()'), /forbidden|permission/);
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query('select desk_member_snapshot()'), /permission denied/);
    await db.query('rollback');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Check-in fallbacks: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
