// Phase 3 Task 4 (D4): the desk refuses a duplicate membership or Day pass sale when the member already has an active term.
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
  [`dup-${++serial}@test.io`, JSON.stringify({ full_name: 'Dup Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const desk = await user('employee');
  await openCashShifts(db);
  const monthly = (await db.query(`insert into membership_packages(name,price,duration_days) values('Dup Monthly',1500,30) returning membership_package_id id`)).rows[0].id;
  const dayPass = (await db.query(`insert into membership_packages(name,price,duration_days,is_day_pass) values('Day pass',150,1,true) returning membership_package_id id`)).rows[0].id;

  await test('desk cannot sell Membership to a member who already has an active membership (Review Focus 2)', async () => {
    const m = await user(null);
    await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, monthly]);
    await assert.rejects(
      as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, monthly]),
      /already_member|already_availed/
    );
    await assert.rejects(
      as(desk, `select * from start_desk_online_payment('membership'::payment_kind,$1,$2,null,null)`, [m, monthly]),
      /already_member|already_availed/
    );
  });

  await test('desk cannot sell Day pass to a member who already has an active membership or Day pass', async () => {
    const m = await user(null);
    await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, dayPass]);
    await assert.rejects(
      as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, dayPass]),
      /already_member|already_availed/
    );
    await assert.rejects(
      as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, monthly]),
      /already_member|already_availed/
    );
  });

  await test('Renewal for a member with an active membership still works', async () => {
    const m = await user(null);
    const p = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, monthly]))[0];
    const r = (await as(desk, `select * from record_counter_payment('renewal'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, p.user_membership_package_id]))[0];
    assert.equal(r.kind, 'renewal');
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally { await db.end(); await server.stop(); }
