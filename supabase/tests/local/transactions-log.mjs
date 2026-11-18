// Plan 2026-11-17 Z4-Z6 (0086): money rows (payment.*, refund.*, cash_shift.*) get their own Transactions log, with the customer,
// the amount and how they paid; the audit log lists the rest. Old apps that send no p_money still get every row.
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
const user = async (role, name) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`tl-${++serial}@test.io`, JSON.stringify({ full_name: name, terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const rows = (uid, money) => as(uid, `select * from audit_records(p_money=>$1, p_limit=>1000)`, [money]);

try {
  const admin = await user('admin', 'Dana Reyes'), desk = await user('employee', 'Marco Santos'), member = await user(null, 'Alex Tan');
  await openCashShifts(db);
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('T Monthly',1200,30) returning membership_package_id id`)).rows[0].id;
  const sale = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [member, pack]))[0];
  const pass = (await as(desk, `select * from record_counter_payment('walk_in'::payment_kind,'cash'::payment_method,null,null,'Pedro Ramos',null,null)`))[0];
  await db.query(`insert into audit_log(action, entity, entity_id, details) values('payment.online','payments',$1,'{"channel":"GCASH"}')`, [String(sale.payment_id)]);
  await db.query(`insert into audit_log(action, entity, entity_id) values('role.grant','user_roles',$1)`, [member]);

  await test('a cash sale row shows the member, the amount and cash', async () => {
    const r = (await rows(admin, true)).find(x => x.action === 'payment.record' && x.entity_id === String(sale.payment_id));
    assert.equal(r.customer, 'Alex Tan'); assert.equal(Number(r.amount), 1200); assert.equal(r.paid_with, 'cash');
  });

  await test('a Day pass row shows its typed name', async () => {
    const r = (await rows(admin, true)).find(x => x.entity_id === String(pass.payment_id));
    assert.equal(r.customer, 'Pedro Ramos');
  });

  await test('a Xendit row says the channel it was paid with', async () => {
    const r = (await rows(admin, true)).find(x => x.action === 'payment.online');
    assert.equal(r.paid_with, 'GCASH');
  });

  await test('Transactions lists only money rows; the audit log none of them', async () => {
    const money = await rows(admin, true), audit = await rows(admin, false);
    assert.ok(money.length >= 3 && money.every(x => ['payment', 'refund', 'cash_shift'].includes(x.action.split('.')[0])));
    assert.ok(audit.some(x => x.action === 'role.grant'));
    assert.ok(audit.every(x => !['payment', 'refund', 'cash_shift'].includes(x.action.split('.')[0])));
  });

  await test('old apps (no p_money) still get every row', async () => {
    const all = await as(admin, `select * from audit_records(p_limit=>1000)`);
    assert.ok(all.some(x => x.action === 'role.grant') && all.some(x => x.action === 'payment.record'));
  });

  await test('members cannot read it and anon cannot call it', async () => {
    await assert.rejects(rows(member, true), /forbidden|permission|not_allowed/);
    const r = (await db.query(`select has_function_privilege('anon','public.audit_records(date,date,text,text,text,integer,integer,boolean)','execute') a`)).rows[0];
    assert.equal(r.a, false);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Transactions log: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
