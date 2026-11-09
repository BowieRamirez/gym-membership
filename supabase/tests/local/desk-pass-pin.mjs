// Phase 2 of the 2026-11-09 plan (F6, F8). F6: the member's QR is a one-use pass from the server that lasts 5 minutes, so a
// screenshot or the old permanent code no longer works, and a QR check-in needs a pass this desk just scanned. F8: a check-in by
// name (or queued offline) needs the member's 4-digit desk PIN; five wrong tries lock it for 15 minutes; only Admin and System
// admin may skip the PIN, and only with a reason.
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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`pin-${++serial}@test.io`, JSON.stringify({ full_name: 'Pin Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee'), other = await user('employee'), owner = await user('admin'), sub = await user('subadmin'), coach = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('Pin Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
async function paid() {
  const uid = await user();
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  return uid;
}
const pass = async uid => (await as(uid, 'select * from issue_member_pass()'))[0];
const scan = async (code, who = desk) => (await as(who, 'select scan_member($1) r', [code]))[0].r;
const checkIn = (uid, extra = {}, who = desk) => as(who,
  'select record_check_in(p_member_id=>$1, p_method=>$2, p_pin=>$3, p_reason=>$4, p_at=>$5, p_client_ref=>$6) id',
  [uid, extra.method ?? null, extra.pin ?? null, extra.reason ?? null, extra.at ?? null, extra.ref ?? null]);
const count = async uid => Number((await db.query('select count(*) n from gym_check_ins where member_id=$1', [uid])).rows[0].n);
const setPin = (uid, pin) => as(uid, 'select set_desk_pin($1)', [pin]);

try {
  // ---- F6: the pass ----
  await test('F6: a member gets a one-use pass that lasts 5 minutes; staff and coaches get none', async () => {
    const uid = await paid();
    const p = await pass(uid);
    assert.match(p.code, /^GYM2-[0-9a-f]{32}$/);
    const minutes = (new Date(p.expires_at) - Date.now()) / 60000;
    assert.ok(minutes > 4.9 && minutes <= 5.01, `lasts ${minutes} minutes`);
    for (const who of [coach, desk]) await assert.rejects(as(who, 'select * from issue_member_pass()'), /forbidden/);
  });
  await test('F6: a pass scans once; the same code again (a screenshot) is refused', async () => {
    const uid = await paid(), p = await pass(uid);
    const first = await scan(p.code);
    assert.equal(first.result, 'active'); assert.equal(first.member, uid);
    const again = await scan(p.code);
    assert.equal(again.result, 'old_code'); assert.equal(again.member, null);
  });
  await test('F6: an expired pass, a replaced pass and the old permanent code are refused', async () => {
    const uid = await paid(), first = await pass(uid), second = await pass(uid);
    assert.equal((await scan(first.code)).result, 'old_code');            // a new pass cancels the unused one
    await db.query(`update member_passes set expires_at=now()-interval '1 second' where code=$1`, [second.code]);
    assert.equal((await scan(second.code)).result, 'old_code');
    const permanent = (await db.query('select code from member_codes where user_id=$1', [uid])).rows[0].code;
    assert.equal((await scan(permanent)).result, 'old_code');
    assert.equal((await scan('GYM2-nope')).result, 'not_found');
  });
  await test('F6: a QR check-in needs a pass this desk scanned in the last 10 minutes, and uses it once', async () => {
    const uid = await paid();
    await assert.rejects(checkIn(uid), /scan_required/);                  // no scan at all
    await assert.rejects(checkIn(uid, { method: 'qr' }), /scan_required/);
    const p = await pass(uid);
    await scan(p.code, other);                                             // another desk's scan doesn't count here
    await assert.rejects(checkIn(uid, { method: 'qr' }), /scan_required/);
    const q = await pass(uid); await scan(q.code);
    await checkIn(uid, { method: 'qr' });
    await assert.rejects(checkIn(uid, { method: 'qr' }), /scan_required/); // the scan is used up
    const r = await pass(uid); await scan(r.code);
    await db.query(`update member_passes set scanned_at=now()-interval '11 minutes' where code=$1`, [r.code]);
    await assert.rejects(checkIn(uid), /scan_required/);
    assert.equal(await count(uid), 1);
    assert.equal((await db.query(`select method from gym_check_ins where member_id=$1`, [uid])).rows[0].method, 'qr');
  });
  await test('F6: nobody reads or writes passes or PINs directly', async () => {
    const uid = await paid(); await pass(uid); await setPin(uid, '1234');
    for (const who of [uid, desk, owner]) {
      await assert.rejects(as(who, 'select * from member_passes'), /permission denied/);
      await assert.rejects(as(who, 'select * from desk_pins'), /permission denied/);
    }
  });
  await test('F6: the desk\'s saved list has names and terms but no codes', async () => {
    await paid();
    const list = (await as(desk, 'select desk_member_snapshot() s'))[0].s;
    assert.ok(list.length > 0);
    assert.ok(list.every(r => !('code' in r) && r.member && r.name));
  });

  // ---- F8: the desk PIN ----
  await test('F8: a member sets a 4-digit PIN, stored hashed; anything else is refused', async () => {
    const uid = await paid();
    for (const bad of ['123', '12345', '12a4', '', null]) await assert.rejects(setPin(uid, bad), /bad_pin/);
    await setPin(uid, '0420');
    const hash = (await db.query('select pin_hash from desk_pins where user_id=$1', [uid])).rows[0].pin_hash;
    assert.notEqual(hash, '0420'); assert.match(hash, /^\$2/);
    await assert.rejects(setPin(desk, '1234'), /forbidden/);
    await assert.rejects(as(coach, `select set_desk_pin('1234')`), /forbidden/);
  });
  await test('F8: a check-in by name works with the right PIN and is refused without one', async () => {
    const uid = await paid(); await setPin(uid, '2468');
    await assert.rejects(checkIn(uid, { method: 'manual' }), /pin_required/);
    await assert.rejects(checkIn(uid, { method: 'manual', reason: 'Trust me' }), /pin_required/);   // the front desk can't skip it
    assert.ok((await checkIn(uid, { method: 'manual', pin: '2468' }))[0].id);
    const r = (await db.query('select method, recorded_by, reason from gym_check_ins where member_id=$1', [uid])).rows[0];
    assert.deepEqual([r.method, r.recorded_by, r.reason], ['manual', desk, null]);
  });
  await test('F8: a member with no PIN can only be checked in by name by an Admin or System admin, with a reason', async () => {
    const uid = await paid();
    await assert.rejects(checkIn(uid, { method: 'manual', pin: '1111' }), /no_desk_pin/);
    await assert.rejects(checkIn(uid, { method: 'manual' }, sub), /pin_required/);              // no reason
    await assert.rejects(checkIn(uid, { method: 'manual', reason: '   ' }, owner), /pin_required/);
    await checkIn(uid, { method: 'manual', reason: 'Known member, phone broken' }, sub);
    await checkIn(uid, { method: 'manual', reason: 'Second visit' }, owner);
    const rows = (await db.query('select recorded_by, reason from gym_check_ins where member_id=$1 order by check_in_id', [uid])).rows;
    assert.deepEqual(rows.map(r => [r.recorded_by, r.reason]), [[sub, 'Known member, phone broken'], [owner, 'Second visit']]);
  });
  await test('F8: a wrong PIN checks no one in and counts; the fifth locks it for 15 minutes, even for the right PIN', async () => {
    const uid = await paid(); await setPin(uid, '1357');
    for (let n = 1; n <= 4; n++) assert.equal((await checkIn(uid, { method: 'manual', pin: '0000' }))[0].id, null);
    assert.equal((await checkIn(uid, { method: 'manual', pin: '9999' }))[0].id, null);
    await assert.rejects(checkIn(uid, { method: 'manual', pin: '1357' }), /pin_locked/);
    assert.equal(await count(uid), 0);
    await db.query(`update desk_pins set locked_until=now()-interval '1 second' where user_id=$1`, [uid]);
    assert.ok((await checkIn(uid, { method: 'manual', pin: '1357' }))[0].id);
  });
  await test('F8: a right PIN clears earlier wrong tries; a new PIN unlocks', async () => {
    const uid = await paid(); await setPin(uid, '1111');
    for (let n = 1; n <= 4; n++) await checkIn(uid, { method: 'manual', pin: '0000' });
    await checkIn(uid, { method: 'manual', pin: '1111' });
    await checkIn(uid, { method: 'manual', pin: '0000' });
    assert.equal((await db.query('select fails from desk_pins where user_id=$1', [uid])).rows[0].fails, 1);
    for (let n = 1; n <= 4; n++) await checkIn(uid, { method: 'manual', pin: '0000' });
    await assert.rejects(checkIn(uid, { method: 'manual', pin: '1111' }), /pin_locked/);
    await setPin(uid, '2222');
    assert.ok((await checkIn(uid, { method: 'manual', pin: '2222' }))[0].id);
  });
  await test('F8: a queued offline check-in carries the PIN and is checked when sent', async () => {
    const uid = await paid(); await setPin(uid, '8642');
    await db.query("update membership_terms set starts_at=now()-interval '1 day' where member_id=$1", [uid]);
    const at = new Date(Date.now() - 3600000).toISOString();
    await assert.rejects(checkIn(uid, { method: 'offline', at, ref: randomUUID() }), /pin_required/);
    assert.equal((await checkIn(uid, { method: 'offline', at, ref: randomUUID(), pin: '1111' }))[0].id, null);
    const ref = randomUUID();
    const id = (await checkIn(uid, { method: 'offline', at, ref, pin: '8642' }))[0].id;
    assert.ok(id);
    assert.equal((await checkIn(uid, { method: 'offline', at, ref, pin: '8642' }))[0].id, id);   // resent: the same one
    assert.equal(await count(uid), 1);
  });
  await test('F8: membership is still checked first, and only staff can check anyone in', async () => {
    const none = await user(); await setPin(none, '1234');
    await assert.rejects(checkIn(none, { method: 'manual', pin: '1234' }), /no_active_membership/);
    const uid = await paid(); await setPin(uid, '1234');
    for (const who of [uid, coach]) await assert.rejects(checkIn(uid, { method: 'manual', pin: '1234' }, who), /forbidden|permission/);
    await assert.rejects(checkIn(uid, { method: 'manual', pin: '1234', reason: 'x'.repeat(501) }, owner), /bad_reason/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Desk pass and PIN: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
