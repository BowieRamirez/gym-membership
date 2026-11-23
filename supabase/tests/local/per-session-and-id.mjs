// Phase 8 of the 2026-11-12 plan (0066, owner decisions G19-G21).
// G19: amenities are sold per session only. No sale path makes a new pass (`pass_not_sold`): the member's purchase, the Cashier,
//      booking an amenity still set up as a pass. Staff can't set up a pass; a pass amenity can still be turned into a per-session one.
//      Passes already bought keep booking until they end.
// G20/G21: record_check_in method 'id' with p_id_type (a fixed list), for staff who record check-ins (front desk, Admin, System admin),
//      for any active member with or without a desk PIN. The type is stored (no ID number), the check-in is audited, and a queued
//      offline check-in can carry it.
import { start, openCashShifts } from './harness.mjs';
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
  [`p8-${++serial}@test.io`, JSON.stringify({ full_name: 'Id Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P8 Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
// set up by the database owner, as amenities sold as passes before 0066 were
const amenity = async (name, days = null) => (await db.query('insert into amenities(name,price,duration_days) values($1,150,$2) returning amenity_id id', [name, days])).rows[0].id;
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
const book = (uid, a, hm) => as(uid, `select * from book_amenity($1,$2,60,'qr'::payment_method)`, [a, at(hm)]).then(r => r[0]);
const idCheckIn = (who, uid, type = 'drivers_license', extra = '') =>
  as(who, `select record_check_in(p_member_id=>$1, p_method=>'id', p_id_type=>$2${extra}) id`, [uid, type]).then(r => r[0].id);

try {
  // ---------- G19 ----------
  await test('a pass can\'t be bought: not by the member, not at the Cashier, not by booking a pass amenity', async () => {
    const uid = await member(), pass = await amenity('P8 Pass', 30);
    await assert.rejects(as(uid, 'select * from avail_amenity($1)', [pass]), /pass_not_sold/);
    await assert.rejects(as(desk, `select * from record_counter_payment('amenity','cash',$1,$2)`, [uid, pass]), /pass_not_sold/);
    await assert.rejects(book(uid, pass, '10:00'), /pass_not_sold/);
    assert.equal((await db.query('select count(*)::int n from user_amenities where member_id=$1', [uid])).rows[0].n, 0);
  });
  await test('a per-session amenity still sells and books as before', async () => {
    const uid = await member(), a = await amenity('P8 Session');
    await as(desk, `select * from record_counter_payment('amenity','cash',$1,$2)`, [uid, a]);
    assert.equal((await book(uid, a, '11:00')).status, 'confirmed');   // the session bought at the desk
    assert.equal((await book(uid, a, '13:00')).status, 'awaiting_payment');   // the next one needs a down payment
  });
  await test('a pass bought before keeps booking straight away until it ends; after that it is per session only', async () => {
    const uid = await member(), pass = await amenity('P8 Old Pass', 30);
    const ua = (await db.query(`insert into user_amenities(member_id,amenity_id,status,expires_at) values($1,$2,'active',now()+interval '5 days') returning user_amenity_id`, [uid, pass])).rows[0].user_amenity_id;
    assert.equal((await book(uid, pass, '09:00')).status, 'confirmed');
    await db.query(`update user_amenities set expires_at=now()+interval '1 day' where user_amenity_id=$1`, [ua]);
    await assert.rejects(book(uid, pass, '12:00'), /pass_not_sold/);
  });
  await test('staff can\'t set up a pass, but can turn a pass amenity into a per-session one', async () => {
    await assert.rejects(as(admin, `insert into amenities(name,price,duration_days) values('P8 New Pass',100,30)`), /pass_not_sold/);
    const a = await amenity('P8 Becomes Pass');
    await assert.rejects(as(admin, 'update amenities set duration_days=30 where amenity_id=$1', [a]), /pass_not_sold/);
    const old = await amenity('P8 Converted', 30);
    await as(admin, 'update amenities set duration_days=null, price=99 where amenity_id=$1', [old]);
    await as(admin, `update amenities set description='Renamed' where amenity_id=$1`, [old]);
    const uid = await member();
    await as(desk, `select * from record_counter_payment('amenity','cash',$1,$2)`, [uid, old]);
  });

  // ---------- G20 / G21 ----------
  await test('front desk, Admin and System admin check a member in by ID; the type and who checked are kept, and it is audited', async () => {
    for (const [who, type] of [[desk, 'drivers_license'], [admin, 'national_id'], [sub, 'passport']]) {
      const uid = await member();
      const id = await idCheckIn(who, uid, type);
      const row = (await db.query('select member_id, recorded_by, method, id_type from gym_check_ins where check_in_id=$1', [id])).rows[0];
      assert.deepEqual(row, { member_id: uid, recorded_by: who, method: 'id', id_type: type });
      const log = (await db.query(`select actor_id, details from audit_log where action='check_in.id' and entity_id=$1`, [String(id)])).rows[0];
      assert.equal(log.actor_id, who);
      assert.equal(log.details.id_type, type);
    }
  });
  await test('a member with a desk PIN can be checked in by ID too (G21)', async () => {
    const uid = await member();
    await as(uid, 'select set_desk_pin($1)', ['1357']);
    assert.ok(await idCheckIn(desk, uid, 'school_id'));
  });
  await test('a member or coach can\'t check anyone in by ID; anon can\'t call it', async () => {
    const uid = await member();
    for (const who of [uid, coach]) await assert.rejects(idCheckIn(who, uid), /forbidden|permission/);
    const g = (await db.query(`select has_function_privilege('anon','public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text)','execute') a`)).rows[0];
    assert.equal(g.a, false);
  });
  await test('a bad or missing ID type is refused, and an ID type goes only with the id or offline method', async () => {
    const uid = await member();
    await assert.rejects(idCheckIn(desk, uid, 'library_card'), /bad_id_type/);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'id') id`, [uid]), /bad_input/);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_id_type=>'passport') id`, [uid]), /bad_input/);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'id', p_id_type=>'passport', p_at=>now()) id`, [uid]), /bad_input/);
    await db.query(`alter table gym_check_ins disable trigger user`);   // the column's own check, not only the function's
    try { await assert.rejects(db.query(`insert into gym_check_ins(member_id,method,id_type) values($1,'id','library_card')`, [uid]), /check/); }
    finally { await db.query(`alter table gym_check_ins enable trigger user`); }
  });
  await test('an ID check-in needs an active membership', async () => {
    const uid = await user();
    await db.query('insert into members(user_id) values($1) on conflict do nothing', [uid]);
    await assert.rejects(idCheckIn(desk, uid), /no_active_membership/);
  });
  await test('a queued offline ID check-in is sent with its type and time', async () => {
    const uid = await member();
    // the term began moments ago, at the database's microsecond now(); JavaScript's time is whole milliseconds, so in the same
    // millisecond it read as just before the term started (no_active_membership, 1 run in a few). Let a few milliseconds pass.
    await new Promise(r => setTimeout(r, 5));
    const when = new Date().toISOString();
    const id = (await as(desk, `select record_check_in(p_member_id=>$1, p_method=>'offline', p_at=>$2, p_client_ref=>gen_random_uuid(), p_id_type=>'other') id`, [uid, when]))[0].id;
    const row = (await db.query('select method, id_type from gym_check_ins where check_in_id=$1', [id])).rows[0];
    assert.deepEqual(row, { method: 'offline', id_type: 'other' });
  });
  await test('PIN, password and QR check-ins work as before with the new argument left out', async () => {
    const uid = await member();
    await as(uid, 'select set_desk_pin($1)', ['2468']);
    await as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'2468') id`, [uid]);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1) id`, [uid]), /scan_required/);
    await assert.rejects(as(desk, `select record_check_in(p_member_id=>$1, p_method=>'password') id`, [uid]), /proof_invalid/);
    await assert.rejects(as(admin, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_reason=>'Lost phone') id`, [uid]), /has_desk_pin/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Per session and ID check-in: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
