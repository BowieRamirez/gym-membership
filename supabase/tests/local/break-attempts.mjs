// Phase 10: trying to break the system through direct calls, as a modified app could. Paying 0 (D4, 0046), check-in without a valid
// membership, buying with a bad package or account, and the functions that had no refusal test yet (D16).
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = [], role = 'authenticated') {
  await db.query('begin');
  try {
    await db.query(`set local role ${role}`);
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid ?? '']);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`b-${++serial}@test.io`, JSON.stringify({ full_name: 'Break Tester' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const id = async (sql, args = []) => Object.values((await db.query(sql, args)).rows[0])[0];
const owner = await user('admin'), desk = await user('employee');
const pack = await id(`insert into membership_packages(name,price,duration_days) values('B Monthly',1000,30) returning membership_package_id`);
const freePack = await id(`insert into membership_packages(name,price,duration_days) values('B Free',0,30) returning membership_package_id`);
const oldPack = await id(`insert into membership_packages(name,price,duration_days,is_active) values('B Old',500,30,false) returning membership_package_id`);
const freeAmenity = await id(`insert into amenities(name,price,duration_days) values('B Free amenity',0,30) returning amenity_id`);
await db.query(`insert into discounts(code,percent,expires_at,max_uses) values('ALLFREE',100,now()+interval '30 days',100)`);
const buy = async (uid, p = pack, c = null) => (await as(uid, 'select * from extend_membership($1,$2)', [p, c]))[0];
async function paid() {
  const uid = await user();
  await as(desk, 'select verify_payment($1,true)', [(await buy(uid)).payment_id]);
  return uid;
}
const codeOf = async uid => id('select code from member_codes where user_id=$1', [uid]);
const scan = async c => (await as(desk, 'select scan_member($1) s', [c]))[0].s.result;
const checkIn = uid => as(desk, `select record_check_in($1,'manual')`, [uid]);

try {
  // ---- D4: paying 0 ----
  await test('D4: a member cannot create a payment of 0 for a free package by calling the function directly', async () => {
    await assert.rejects(buy(await user(), freePack), /zero_amount/);
  });
  await test('D4: a 100% discount code may bring the price to 0', async () => {
    const pay = await buy(await user(), pack, 'ALLFREE');
    assert.deepEqual([Number(pay.amount), Number(pay.discount_amount)], [0, 1000]);
  });
  await test('D4: the desk cannot record 0 for a walk-in, a free amenity or a free package', async () => {
    await db.query('update gym_settings set walk_in_price=0 where id=1');
    await assert.rejects(as(desk, `select * from record_counter_payment('walk_in','cash',null,null,'Juan Cruz')`), /zero_amount/);
    await db.query('update gym_settings set walk_in_price=150 where id=1');
    assert.equal(Number((await as(desk, `select * from record_counter_payment('walk_in','cash',null,null,'Juan Cruz')`))[0].amount), 150);
    const member = await paid();
    await assert.rejects(as(desk, `select * from record_counter_payment('amenity','cash',$1,$2)`, [member, freeAmenity]), /zero_amount/);
    await assert.rejects(as(desk, `select * from record_counter_payment('membership','cash',$1,$2)`, [await user(), freePack]), /zero_amount/);
  });
  await test('D4: a member cannot buy a free amenity, and a refused purchase leaves nothing behind', async () => {
    const member = await paid();
    await assert.rejects(as(member, 'select * from avail_amenity($1)', [freeAmenity]), /zero_amount/);
    assert.equal(await id('select count(*)::int from user_amenities where member_id=$1', [member]), 0);
  });
  await test('negative amounts and direct writes to payments are refused', async () => {
    const member = await user();
    await assert.rejects(as(member, `insert into payments(payment_code,kind,method,amount,member_id) values('X-1','membership','qr',-5,$1)`, [member]), /permission denied/);
    // even the database owner, past every function: a walk-in row is the one kind with nothing else to point at
    const walkIn = (code, amount) => db.query(`insert into payments(payment_code,kind,method,amount,walk_in_name) values($1,'walk_in','cash',$2,'Juan Cruz')`, [code, amount]);
    await assert.rejects(walkIn('X-2', -5), /zero_amount|check constraint/);   // 0046 stops it first; the 0002 constraint is behind it
    await assert.rejects(walkIn('X-3', 0), /zero_amount/);
    const verified = await buy(await user(), pack, 'ALLFREE');
    await assert.rejects(db.query('update payments set amount=-1 where payment_id=$1', [verified.payment_id]), /check constraint/);
  });

  // ---- check-in without a valid membership, by scan and by name ----
  await test('check-in: an unknown or hostile QR code is not found', async () => {
    assert.equal(await scan('NOT-A-REAL-CODE'), 'not_found');
    assert.equal(await scan(`x'; delete from gym_check_ins; --`), 'not_found');
    assert.equal(await scan(''), 'not_found');
  });
  await test('check-in: a member who never paid is refused', async () => {
    const uid = await user();
    assert.equal(await scan(await codeOf(uid)), 'expired');
    await assert.rejects(checkIn(uid), /no_active_membership/);
  });
  await test('check-in: a member whose payment is pending is refused', async () => {
    const uid = await user();
    await buy(uid);
    assert.equal(await scan(await codeOf(uid)), 'pending');
    await assert.rejects(checkIn(uid), /no_active_membership/);
  });
  await test('check-in: an expired member is refused', async () => {
    const uid = await paid();
    await db.query(`update membership_terms set starts_at=now()-interval '40 days', ends_at=now()-interval '10 days' where member_id=$1`, [uid]);
    assert.equal(await scan(await codeOf(uid)), 'expired');
    await assert.rejects(checkIn(uid), /no_active_membership/);
  });
  await test('check-in: an archived account is refused even with time left on its membership', async () => {
    const uid = await paid();
    await as(owner, 'select archive_account($1,$2)', [uid, 'Phase 10 check']);
    assert.equal(await scan(await codeOf(uid)), 'inactive');
    await assert.rejects(checkIn(uid), /no_active_membership|account_archived|inactive/);
  });
  await test('check-in: an unknown member id is refused, and only staff can check anyone in', async () => {
    await assert.rejects(checkIn('00000000-0000-0000-0000-000000000000'), /no_active_membership|not_found/);
    const uid = await paid();
    await assert.rejects(as(uid, `select record_check_in($1,'manual')`, [uid]), /forbidden|permission/);
    await assert.rejects(as(uid, 'select scan_member($1)', [await codeOf(uid)]), /forbidden|permission/);
  });

  // ---- buying with a bad package or account ----
  await test('extend_membership refuses an unknown or inactive package, a second pending payment and an archived account', async () => {
    const uid = await user();
    await assert.rejects(as(uid, 'select * from extend_membership($1,null)', [999999]), /not_found/);
    await assert.rejects(buy(uid, oldPack), /not_found/);
    await buy(uid);
    await assert.rejects(buy(uid), /renewal_pending/);
    const gone = await user();
    await as(owner, 'select archive_account($1,$2)', [gone, 'Phase 10 check']);
    await assert.rejects(buy(gone), /account_archived|inactive|forbidden|not_found/);
  });

  // ---- the rest of the functions without a refusal test (D16) ----
  await test('is_active_member: nobody signed out can ask', async () => {
    await assert.rejects(as(null, 'select is_active_member($1)', [desk], 'anon'), /permission denied/);
  });
  await test('verify_amenity_usage: a member cannot verify a visit', async () => {
    const uid = await paid();
    await assert.rejects(as(uid, 'select verify_amenity_usage(1,true)'), /forbidden|permission/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Break attempts: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
