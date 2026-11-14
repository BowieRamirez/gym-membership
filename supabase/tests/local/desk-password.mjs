// Phase 5 of the 2026-11-12 plan (G12, 0062): login mode. A member at the desk with no phone and no desk PIN types their account
// password. The Edge Function verify-member-password checks it with Supabase Auth (not testable here); these checks cover the two
// database steps it calls as the service role, and record_check_in's new 'password' method:
//   desk_password_target(desk, member) -> the member's sign-in email, if the desk may check them in and they aren't locked out
//   desk_password_result(desk, member, ok) -> a one-use proof for 2 minutes, or a wrong try counted with the desk PIN's (0049)
// plus a static look at the function's source: it never logs or stores the password and signs in with a separate client.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import fs from 'node:fs';

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
async function asService(sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role service_role');
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`pw-${++serial}@test.io`, JSON.stringify({ full_name: 'Pass Word' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee'), other = await user('employee'), owner = await user('admin'), coach = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('Pw Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
async function paid() {
  const uid = await user();
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  return uid;
}
const target = (member, who = desk) => asService('select desk_password_target($1,$2) email', [who, member]).then(r => r[0].email);
const result = (member, ok, who = desk) => asService('select desk_password_result($1,$2,$3) r', [who, member, ok]).then(r => r[0].r);
const checkIn = (uid, proof, who = desk, extra = {}) => as(who,
  'select record_check_in(p_member_id=>$1, p_method=>$2, p_proof=>$3, p_at=>$4) id', [uid, extra.method ?? 'password', proof, extra.at ?? null]);
const count = async uid => Number((await db.query('select count(*) n from gym_check_ins where member_id=$1', [uid])).rows[0].n);

try {
  await test('the target is the member\'s sign-in email, for a desk that records check-ins', async () => {
    const uid = await paid();
    assert.match(await target(uid), /^pw-\d+@test\.io$/);
    assert.match(await target(uid, owner), /@test\.io$/);
  });
  await test('only members: a coach or desk account, or nobody, is not a target; a coach or member desk is refused', async () => {
    const uid = await paid();
    for (const who of [coach, other]) await assert.rejects(target(who), /not_found/);
    await assert.rejects(target('00000000-0000-0000-0000-000000000000'), /not_found/);
    for (const who of [coach, uid]) await assert.rejects(target(uid, who), /forbidden/);
  });
  await test('a right password gives a proof that checks the member in once, as method password', async () => {
    const uid = await paid();
    const r = await result(uid, true);
    assert.ok(r.proof, JSON.stringify(r));
    await checkIn(uid, r.proof);
    assert.equal((await db.query(`select method from gym_check_ins where member_id=$1`, [uid])).rows[0].method, 'password');
    await assert.rejects(checkIn(uid, r.proof), /proof_invalid/);    // used up
  });
  await test('a proof is for that member, from that desk, for 2 minutes, and never for an offline time', async () => {
    const a = await paid(), b = await paid();
    const p = (await result(a, true)).proof;
    await assert.rejects(checkIn(b, p), /proof_invalid/);              // another member
    await assert.rejects(checkIn(a, p, other), /proof_invalid/);       // another desk
    await assert.rejects(checkIn(a, p, desk, { at: new Date().toISOString() }), /bad_input/);
    await db.query(`update desk_password_proofs set expires_at=now()-interval '1 second' where proof=$1`, [p]);
    await assert.rejects(checkIn(a, p), /proof_invalid/);              // expired
    await assert.rejects(checkIn(a, null), /proof_invalid/);           // no proof at all
    assert.equal(await count(a), 0);
  });
  await test('wrong passwords count with the desk PIN: the fifth locks the member out for 15 minutes, right password too', async () => {
    const uid = await paid();                                          // no desk PIN set: the tries still count
    for (let i = 0; i < 4; i++) assert.deepEqual(await result(uid, false), { error: 'wrong_password' });
    assert.deepEqual(await result(uid, false), { error: 'pin_locked' });
    await assert.rejects(target(uid), /pin_locked/);
    await assert.rejects(result(uid, true), /pin_locked/);
    const lock = (await db.query('select locked_until from desk_pins where user_id=$1', [uid])).rows[0].locked_until;
    const minutes = (new Date(lock) - Date.now()) / 60000;
    assert.ok(minutes > 14.9 && minutes <= 15.01, `locked ${minutes} minutes`);
    // the shared lock also stops the PIN (0049), and a member without a PIN still has none
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'1234') id`, [uid]), /pin_locked/);
  });
  await test('a right password clears the wrong tries; the member\'s own PIN keeps working', async () => {
    const uid = await paid();
    await as(uid, 'select set_desk_pin($1)', ['2468']);
    await result(uid, false); await result(uid, false);
    assert.ok((await result(uid, true)).proof);
    assert.equal((await db.query('select fails from desk_pins where user_id=$1', [uid])).rows[0].fails, 0);
    await as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'2468') id`, [uid]);
  });
  await test('a right password for a member with no active membership gives a proof, but the check-in is refused', async () => {
    const uid = await user();
    const p = (await result(uid, true)).proof;
    await assert.rejects(checkIn(uid, p), /no_active_membership/);
  });
  await test('the two steps are the service role\'s only: the app, staff included, can\'t call them or read the proofs', async () => {
    const uid = await paid();
    for (const who of [desk, owner, uid]) {
      await assert.rejects(as(who, 'select desk_password_target($1,$2)', [desk, uid]), /permission denied/);
      await assert.rejects(as(who, 'select desk_password_result($1,$2,true)', [desk, uid]), /permission denied/);
      await assert.rejects(as(who, 'select * from desk_password_proofs'), /permission denied/);
    }
    for (const fn of ['desk_password_target(uuid,uuid)', 'desk_password_result(uuid,uuid,boolean)']) {
      const g = (await db.query(`select has_function_privilege('anon','public.${fn}','execute') a, has_function_privilege('authenticated','public.${fn}','execute') b`)).rows[0];
      assert.deepEqual(g, { a: false, b: false }, fn);
    }
    const rc = (await db.query(`select has_function_privilege('anon','public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid)','execute') a`)).rows[0];
    assert.equal(rc.a, false);
  });
  await test('PIN and QR check-ins work as before (0049) with the new argument left out', async () => {
    const uid = await paid();
    await as(uid, 'select set_desk_pin($1)', ['1357']);
    await as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'1357') id`, [uid]);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1) id`, [uid]), /scan_required/);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'password', p_pin=>'1357') id`, [uid]), /proof_invalid/);
  });
  await test('the Edge Function signs in with a separate client, signs that session out, and never logs or stores the password', async () => {
    const src = fs.readFileSync(new URL('../../functions/verify-member-password/index.ts', import.meta.url), 'utf8');
    assert.match(src, /persistSession: false/);
    assert.match(src, /signInWithPassword\(/);
    assert.match(src, /auth\.admin\.signOut\(/);
    assert.match(src, /desk_password_target/);
    assert.match(src, /desk_password_result/);
    assert.doesNotMatch(src, /console\.(log|info|warn|error|debug)/);
    assert.doesNotMatch(src, /from\('.*'\)\.(insert|update|upsert)/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Desk password: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
