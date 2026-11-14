// Owner, 2026-11-11 (before Phase 8): the desk takes Cash or GCash only. 0054 blocked bank transfer; 0055 removes it from the
// payment_method type altogether (the live database had no bank-transfer rows). Everything built on the type must still work, with the
// same rules and the same execute grants.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

// Supabase grants execute to anon, authenticated and service_role on every new function (its default privileges). 0055 recreated
// functions under that, which the plain harness doesn't do; switch it on before 0055 so this test sees what the live project saw.
const { server, client: db } = await start({ beforeMigration: async (db, name) => {
  if (name.startsWith('0055_')) await db.query('alter default privileges in schema public grant execute on functions to anon, authenticated, service_role');
} });
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
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`pay-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin'), desk = await user('employee', 'Desk Dana');
  await openCashShifts(db);
  const monthly = (await db.query(`insert into membership_packages(name,price,duration_days) values('Monthly',1500,30) returning membership_package_id`)).rows[0].membership_package_id;

  await test('the payment method list is QR, cash and GCash: bank transfer is gone, with no old type left over', async () => {
    const labels = (await db.query(`select enumlabel from pg_enum where enumtypid = 'public.payment_method'::regtype order by enumsortorder`)).rows.map(r => r.enumlabel);
    assert.deepEqual(labels, ['qr', 'cash', 'gcash']);
    assert.equal((await db.query(`select count(*)::int n from pg_type where typname like 'payment_method_old%'`)).rows[0].n, 0);
    assert.equal((await db.query(`select count(*)::int n from pg_trigger where tgname like '%no_bank_transfer'`)).rows[0].n, 0);   // 0054's guard is not needed
  });

  await test('the desk can no longer take a bank transfer', async () => {
    await assert.rejects(as(desk, `select * from record_counter_payment('walk_in','bank_transfer',null,null,'Bank Ben','BT-1',null)`), /invalid input value for enum/);
  });

  await test('cash and GCash still work at the desk', async () => {
    await as(desk, `select * from record_counter_payment('walk_in','cash',null,null,'Cash Cara',null,null)`);
    await as(desk, `select * from record_counter_payment('walk_in','gcash',null,null,'G Gina','REF-G-1',null)`);
  });

  await test('a refund is repaid by cash or GCash, never by bank transfer', async () => {
    const member = await user('member', 'Refund Rita');
    const pay = (await as(member, 'select * from extend_membership($1,null)', [monthly]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    const term = (await as(member, 'select * from membership_terms'))[0];
    const req = (await as(member, 'select * from request_membership_refund($1,$2,$3)', [term.term_id, 'Moving away', 'change_of_mind']))[0];
    await as(admin, 'select decide_membership_refund($1,true,$2)', [req.refund_request_id, 'Approved']);
    await assert.rejects(as(admin, `select record_membership_refund_paid($1,'bank_transfer','BT-9')`, [req.refund_request_id]), /invalid input value for enum/);
    await as(admin, `select record_membership_refund_paid($1,'gcash','REF-R-1')`, [req.refund_request_id]);
  });

  await test('the revenue reports still run and show the method', async () => {
    const day = d => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);   // yesterday to tomorrow: Manila's day whatever the clock
    await as(admin, `select * from revenue_summary($1,$2)`, [day(-1), day(1)]);
    const lines = await as(admin, `select * from revenue_lines($1,$2,0,100)`, [day(-1), day(1)]);
    assert.ok(lines.some(l => l.method === 'gcash'));
    await as(admin, `select * from membership_refund_outflows($1,$2)`, [day(-1), day(1)]);
  });

  await test('the table rules came back unchanged: a walk-in cannot be a QR payment', async () => {
    await assert.rejects(db.query(`insert into payments(payment_code,kind,method,status,amount,walk_in_name,expires_at)
      values('GYM-QRWLK','walk_in','qr','pending',150,'QR Walk',now()+interval '1 day')`), /payments_walk_in_at_counter/);
  });

  await test('execute grants are as before 0055 (0056): signed-in users only, never anon; the old counter function closed to both', async () => {
    const can = async (role, fn) => (await db.query(`select has_function_privilege($1, $2, 'execute') ok`, [role, fn])).rows[0].ok;
    const app = ['public.record_counter_payment(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)',
      'public.book_amenity(bigint,timestamptz,integer,public.payment_method,uuid,text)', 'public.record_membership_refund_paid(bigint,public.payment_method,text)',
      'public.revenue_lines(date,date,integer,integer)', 'public.revenue_summary(date,date)', 'public.membership_refund_outflows(date,date)'];
    for (const fn of app) {
      assert.equal(await can('authenticated', fn), true, `${fn} closed to signed-in users`);
      assert.equal(await can('anon', fn), false, `${fn} open to anon`);
    }
    const old = 'public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)';
    assert.equal(await can('authenticated', old), false, 'old counter function open to signed-in users');
    assert.equal(await can('anon', old), false, 'old counter function open to anon');
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nCash and GCash only: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
