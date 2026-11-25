// Owner fixes 2026-11-25, Fix 5 (X1-X4): expenses the gym spends, voided not deleted, and the payroll cost the financial report reads
// from approved pay periods (so payroll is never entered twice).
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
async function asAnon(sql, args = []) {
  await db.query('begin');
  try { await db.query('set local role anon'); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`exp-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin', 'Sys Admin');
const sub = await user('subadmin', 'Sub Admin');
const desk = await user('employee', 'Desk Ana');
const coach = await user('coach', 'Coach Ben');
const member = await user('member', 'Member Cy');

try {
  let rentId;
  await test('X3: an expense is added with its day, category, amount and what it was for, and audited', async () => {
    const e = (await as(admin, `select * from expense_add('2026-10-05','rent',15000,'October rent')`))[0];
    rentId = e.expense_id;
    assert.equal(e.category, 'rent'); assert.equal(Number(e.amount), 15000); assert.equal(e.note, 'October rent');
    assert.equal((await as(sub, `select * from expense_add('2026-10-06','electricity',4200.50,'Meralco bill')`))[0].category, 'electricity');
    const log = await db.query(`select 1 from audit_log where action = 'expense.added'`);
    assert.equal(log.rows.length, 2);
  });
  await test('X3: bad input is refused (category, amount, note, a day in the future)', async () => {
    await assert.rejects(as(admin, `select * from expense_add('2026-10-05','snacks',10,'x')`), /bad_input/);
    await assert.rejects(as(admin, `select * from expense_add('2026-10-05','rent',0,'x')`), /bad_input/);
    await assert.rejects(as(admin, `select * from expense_add('2026-10-05','rent',10000001,'x')`), /bad_input/);
    await assert.rejects(as(admin, `select * from expense_add('2026-10-05','rent',10,'  ')`), /bad_input/);
    await assert.rejects(as(admin, `select * from expense_add((now() at time zone 'Asia/Manila')::date + 1,'rent',10,'x')`), /bad_input/);
  });
  await test('expenses_list shows the days asked for, newest first, with who added it', async () => {
    const rows = await as(admin, `select * from expenses_list('2026-10-01','2026-10-31')`);
    assert.deepEqual(rows.map(r => r.category), ['electricity', 'rent']);
    assert.equal(rows[1].created_by_name, 'Sys Admin');
    assert.equal((await as(admin, `select * from expenses_list('2026-11-01','2026-11-30')`)).length, 0);
    await assert.rejects(as(admin, `select * from expenses_list('2026-10-31','2026-10-01')`), /bad_input/);
  });
  await test('X3: a mistake is voided with a reason, kept, and can\'t be voided twice', async () => {
    await assert.rejects(as(admin, `select expense_void($1,' ')`, [rentId]), /bad_input/);
    await as(admin, `select expense_void($1,'Entered twice')`, [rentId]);
    const r = (await as(admin, `select * from expenses_list('2026-10-01','2026-10-31')`)).find(x => x.expense_id == rentId);
    assert.equal(r.void_reason, 'Entered twice'); assert.ok(r.voided_at); assert.equal(r.voided_by_name, 'Sys Admin');
    await assert.rejects(as(admin, `select expense_void($1,'again')`, [rentId]), /already_processed/);
    await assert.rejects(as(admin, `select expense_void(999999,'x')`), /not_found/);
  });
  await test('X4: payroll_cost is the gross pay of approved or paid-out periods, on the period\'s last day; a draft is left out', async () => {
    await as(admin, `select set_staff_salary($1, 9000, 'fixed')`, [desk]);
    const aug = (await as(admin, `select payroll_period('2026-08-01') p`))[0].p.period_id;
    await as(admin, `select payroll_period('2026-08-16') p`);   // stays a draft
    await as(admin, `select payroll_add_line($1,$2,'bonus',500,'Top seller','regular_bonus')`, [aug, desk]);
    await as(admin, `select payroll_mark_paid($1)`, [aug]);
    const rows = await as(admin, `select day::text, gross from payroll_cost('2026-08-01','2026-08-31')`);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].day, '2026-08-15');
    assert.equal(Number(rows[0].gross), 9500);
    assert.equal((await as(admin, `select * from payroll_cost('2026-09-01','2026-09-30')`)).length, 0);
  });
  await test('X1: front desk, coach, member and anon are refused; the table is closed to the app', async () => {
    for (const who of [desk, coach, member]) {
      await assert.rejects(as(who, `select * from expense_add('2026-10-05','rent',1,'x')`), /forbidden/);
      await assert.rejects(as(who, `select expense_void(1,'x')`), /forbidden/);
      await assert.rejects(as(who, `select * from expenses_list('2026-10-01','2026-10-31')`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_cost('2026-10-01','2026-10-31')`), /forbidden/);
      await assert.rejects(as(who, `select * from expenses`), /permission denied/);
    }
    await assert.rejects(asAnon(`select * from expenses_list('2026-10-01','2026-10-31')`), /permission denied|forbidden/);
    await assert.rejects(asAnon(`select * from payroll_cost('2026-10-01','2026-10-31')`), /permission denied|forbidden/);
    await assert.rejects(as(admin, `insert into expenses(spent_on,category,amount,note,created_by) values ('2026-10-01','rent',1,'x',$1)`, [admin]), /permission denied/);
  });
} finally {
  console.log(`Expenses: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
