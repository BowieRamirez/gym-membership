// Owner, 2026-11-25: "only have withholding tax when the salary exceeds 20,833 PHP". Tax is now worked out once a month, in the
// 16th-end pay, on the whole month's taxable pay with BIR's monthly table (0160); the 1st-15th pay has none, like the contributions.
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
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`mt-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const one = async sql => Number((await db.query(sql)).rows[0].v);
const admin = await user('admin', 'Sys Admin');
const fixed = await user('employee', 'Desk Fixed');
const hourly = await user('employee', 'Desk Hourly');
const t = (day, hm) => `2026-09-${day}T${hm}:00+08:00`;
const row = async (start, uid) => (await as(admin, `select payroll_period($1) p`, [start]))[0].p.rows.find(r => r.user_id === uid);
const n = v => Number(v);

try {
  await test('the monthly BIR table: nothing up to 20,833, then 15% over it, and so on', async () => {
    assert.equal(await one(`select public.payroll_tax(20833) v`), 0);
    assert.equal(await one(`select public.payroll_tax(20834) v`), 0.15);
    assert.equal(await one(`select public.payroll_tax(33333) v`), 1875);
    assert.equal(await one(`select public.payroll_tax(40000) v`), 1875 + 0.2 * 6667);
    assert.equal(await one(`select public.payroll_tax(66667) v`), 8541.8);
    assert.equal(await one(`select public.payroll_tax(700000) v`), 195208.35);   // 183,541.80 + 35% of 33,333
    await assert.rejects(as(admin, `select public.payroll_tax(1)`), /permission denied/);
  });
  await test('the 1st-15th pay has no tax; the 16th-end pay taxes the month (13,000 + 13,000, contributions 2,150)', async () => {
    await as(admin, `select set_staff_salary($1, 13000, 'fixed')`, [fixed]);
    const a = await row('2026-09-01', fixed), b = await row('2026-09-16', fixed);
    assert.equal(n(a.tax), 0);
    assert.equal(n(b.sss) + n(b.philhealth) + n(b.pagibig), 2150);
    assert.equal(n(b.taxable), 23850);                    // 26,000 - 2,150
    assert.equal(n(b.tax), 452.55);                       // (23,850 - 20,833) x 15%
  });
  await test('owner\'s case: 15,600 earned in the 16th-end half only is under 20,833 a month, so no tax', async () => {
    await as(admin, `select set_staff_salary($1, 130, 'hourly')`, [hourly]);
    const id = (await db.query(`insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted)
      values ($1,$2,$3,0,0,0,0,0) returning shift_id`, [hourly, t('17', '08:00'), t('17', '20:00')])).rows[0].shift_id;
    const sep2 = (await as(admin, `select payroll_period('2026-09-16') p`))[0].p.period_id;
    await as(admin, `select payroll_review_time($1,'cash_shift',$2,'approved',null)`, [sep2, id]);
    await as(admin, `select * from payroll_add_hours($1,$2,108,'Owner test: 120 h in the half')`, [sep2, hourly]);
    const b = await row('2026-09-16', hourly);
    assert.equal(n(b.base_pay), 15600);
    assert.equal(n(b.tax), 0);
  });
  await test('tax already taken in an approved 1st-15th pay (before 0160) is taken off the month\'s tax, never twice', async () => {
    const big = await user('employee', 'Desk Big');
    await as(admin, `select set_staff_salary($1, 20000, 'fixed')`, [big]);
    const oct1 = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p.period_id;
    await as(admin, `select payroll_mark_paid($1)`, [oct1]);
    await db.query(`insert into payroll_lines(period_id, user_id, kind, amount, note, category, created_by) values ($1,$2,'deduction',500,'Withholding tax (old rule)','tax',$3)`, [oct1, big, admin]);
    const b = await row('2026-10-16', big);
    const month = await one(`select public.payroll_tax(${n(b.taxable)}) v`);
    assert.equal(n(b.taxable), 40000 - n(b.sss) - n(b.philhealth) - n(b.pagibig));
    assert.equal(n(b.tax), Math.round((month - 500) * 100) / 100);
  });
} finally {
  console.log(`Payroll monthly tax: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
