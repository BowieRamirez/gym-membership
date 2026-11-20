// Owner fixes 2026-11-19 Q6: members pay online only. Cash is recorded by the front desk against the member, and the audit log
// names the staff member who took it. An older app that still sends 'cash' for a package or a booking is refused.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args); await db.query('commit'); return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`oo-${++serial}@test.io`, JSON.stringify({ full_name: 'Online Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  const desk = await user('employee');
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30' where id=1`);
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('OO Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const court = (await db.query(`insert into amenities(name,price) values('OO Court',300) returning amenity_id id`)).rows[0].id;
  const count = async () => (await db.query('select count(*)::int n from payments')).rows[0].n;
  const member = async () => { const m = await user(null); const p = (await as(m, 'select * from avail_membership($1,null)', [pack]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return m; };
  const at = async (hm) => (await db.query(`select (((now() at time zone 'Asia/Manila')::date + 2) + $1::time) at time zone 'Asia/Manila' t`, [hm])).rows[0].t.toISOString();

  await test('a member choosing cash for a new package is refused; nothing is made', async () => {
    const m = await user(null); const before = await count();
    await assert.rejects(as(m, `select * from avail_membership($1,null,'cash')`, [pack]), /online_only/);
    assert.equal(await count(), before);
  });

  await test('a member choosing cash to extend or renew is refused', async () => {
    const m = await member();
    await assert.rejects(as(m, `select * from extend_membership($1,null,'cash')`, [pack]), /online_only/);
    const ump = (await db.query('select user_membership_package_id id from user_membership_packages where member_id=$1', [m])).rows[0].id;
    await assert.rejects(as(m, `select * from renew_membership($1,null,'cash')`, [ump]), /online_only/);
  });

  await test('a member booking an amenity with cash is refused; online still books', async () => {
    const m = await member(); const before = await count();
    await assert.rejects(as(m, `select * from book_amenity($1,$2,60,'cash')`, [court, await at('10:00')]), /online_only/);
    assert.equal(await count(), before);
    assert.equal((await as(m, `select * from book_amenity($1,$2,60,'qr')`, [court, await at('10:00')]))[0].status, 'awaiting_payment');
  });

  await test('the desk records a member cash payment against them, and the audit log names the staff member', async () => {
    const m = await user(null);
    const p = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, pack]))[0];
    assert.equal(p.member_id, m);
    const a = (await db.query(`select actor_id from audit_log where action = 'payment.record' and entity_id = $1`, [String(p.payment_id)])).rows;
    assert.equal(a.length, 1); assert.equal(a[0].actor_id, desk);
  });
  await test('Q8: a member reads only their own payments (the Payments page reads the table directly)', async () => {
    const one = await member(), two = await member();
    const seen = await as(one, 'select member_id from payments');
    assert.ok(seen.length >= 1 && seen.every(r => r.member_id === one));
    assert.ok(!(await as(one, 'select payment_id from payments where member_id = $1', [two])).length);
  });
} finally {
  console.log(`\nonline-only: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
