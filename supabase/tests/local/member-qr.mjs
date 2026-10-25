// Phase 10 (D12): every member has one permanent QR code (a random code, nothing else). Only front desk and admins can look
// it up; the lookup reads live status (inactive > active > pending > expired), never changes the code, and logs every scan.
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
    values($1,'{"full_name":"Qr Person"}',$2) returning id`, [`p10-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), member = await user(), other = await user();
const codeOf = async uid => (await db.query('select code from member_codes where user_id=$1', [uid])).rows[0]?.code;
const scan = async (who, code) => (await as(who, 'select scan_member($1) r', [code]))[0].r;
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P10 Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;

try {
  await test('a new member gets a random code at sign-up; staff and coaches get none', async () => {
    assert.match(await codeOf(member), /^GYM1-[0-9a-f]{32}$/);
    assert.notEqual(await codeOf(member), await codeOf(other));
    assert.equal(await codeOf(coach), undefined);
    assert.equal(await codeOf(desk), undefined);
  });
  await test('a member reads only their own code', async () => {
    const rows = await as(member, 'select user_id, code from member_codes');
    assert.equal(rows.length, 1); assert.equal(rows[0].user_id, member);
    await assert.rejects(as(member, `update member_codes set code='x' where user_id=$1`, [member]), /permission denied|42501/);
  });
  await test('only front desk and admins can scan; members and coaches are refused', async () => {
    const code = await codeOf(member);
    await assert.rejects(as(member, 'select scan_member($1)', [code]), /forbidden/);
    await assert.rejects(as(coach, 'select scan_member($1)', [code]), /forbidden/);
    assert.equal((await scan(desk, code)).result, 'expired');   // no membership yet
    assert.equal((await scan(admin, code)).name, 'Qr Person');
  });
  const before = await codeOf(member);
  let pay;
  await test('a waiting membership payment scans as pending; once verified, active with its end date', async () => {
    pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
    const p = await scan(desk, before);
    assert.equal(p.result, 'pending'); assert.equal(p.expires, null);
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    const a = await scan(desk, before);
    assert.equal(a.result, 'active'); assert.equal(a.member, member);
    const end = (await db.query('select max(ends_at) e from membership_terms where member_id=$1', [member])).rows[0].e;
    assert.equal(new Date(a.expires).getTime(), new Date(end).getTime());
  });
  await test('an extension queued after the current term moves the shown end date', async () => {
    const next = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [next.payment_id]);
    const end = (await db.query('select max(ends_at) e from membership_terms where member_id=$1', [member])).rows[0].e;
    assert.equal(new Date((await scan(desk, before)).expires).getTime(), new Date(end).getTime());
  });
  await test('archived scans as inactive, restored as active again, with the same code throughout', async () => {
    await db.query('update profiles set is_active=false where id=$1', [member]);
    assert.equal((await scan(desk, before)).result, 'inactive');
    await db.query('update profiles set is_active=true where id=$1', [member]);
    assert.equal((await scan(desk, before)).result, 'active');
    assert.equal(await codeOf(member), before);
  });
  await test('an unknown or damaged code is not found and does not throw', async () => {
    const r = await scan(desk, 'GYM1-nope');
    assert.equal(r.result, 'not_found'); assert.equal(r.member, null);
    assert.equal((await scan(desk, '')).result, 'not_found');
  });
  await test('every scan is logged with the scanner, member and result; only admins read the log', async () => {
    const log = (await db.query('select * from member_scans order by scan_id')).rows;
    assert.ok(log.length >= 8);
    assert.ok(log.some(l => l.scanned_by === desk && l.member_id === member && l.result === 'inactive'));
    assert.ok(log.some(l => l.member_id === null && l.result === 'not_found' && l.code_seen === 'GYM1-nope'));
    assert.equal((await as(admin, 'select count(*)::int n from member_scans'))[0].n, log.length);
    assert.equal((await as(desk, 'select count(*)::int n from member_scans'))[0].n, 0);
    assert.equal((await as(member, 'select count(*)::int n from member_scans'))[0].n, 0);
    await assert.rejects(as(desk, `insert into member_scans(scanned_by,code_seen,result) values($1,'x','active')`, [desk]), /permission denied|42501/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nMember QR: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
