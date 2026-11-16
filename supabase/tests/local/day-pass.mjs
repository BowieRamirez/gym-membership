// Cashier plan Y3 (0081, owner's walk-in document, 2026-11-16): a Day pass needs no account and no name. The cashier may type a name
// or leave it blank; a blank one is recorded as "Day pass". Cash at the desk or GCash / QR Ph through Xendit, as any desk sale.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args); await db.query('commit'); return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values('dp-desk@test.io','{"full_name":"Desk Person"}','{"role":"employee"}') returning id`)).rows[0].id;
await openCashShifts(db);

try {
  await test('a cash Day pass with no name is recorded as "Day pass" and checks the person in', async () => {
    const p = (await as(desk, `select * from record_counter_payment('walk_in'::payment_kind,'cash'::payment_method,null,null,'  ',null,null)`))[0];
    assert.deepEqual([p.status, p.walk_in_name], ['verified', 'Day pass']);
    assert.equal((await db.query('select count(*) n from gym_check_ins where payment_id=$1', [p.payment_id])).rows[0].n, '1');
  });
  await test('a typed name is kept', async () => {
    const p = (await as(desk, `select * from record_counter_payment('walk_in'::payment_kind,'cash'::payment_method,null,null,'Pedro Ramos',null,null)`))[0];
    assert.equal(p.walk_in_name, 'Pedro Ramos');
  });
  await test('a Day pass through Xendit at the desk may have no name too', async () => {
    const p = (await as(desk, `select * from start_desk_online_payment('walk_in'::payment_kind,null,null,null,null)`))[0];
    assert.deepEqual([p.status, p.walk_in_name], ['pending', 'Day pass']);
  });
} finally {
  console.log(`\nday-pass: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
