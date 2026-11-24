// Payroll exemptions, non-taxable pay, reopen before payout, retro pay (plan 2026-11-24-payroll-exemptions-reopen.md, U1-U12).
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
    [`pe-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const n = v => Number(v);
const period = async s => (await as(admin, `select payroll_period($1) p`, [s]))[0].p;
const row = async (s, uid) => (await period(s)).rows.find(r => r.user_id === uid);
const line = (p, uid, kind, amount, note, cat) => as(admin, `select * from payroll_add_line($1,$2,$3,$4,$5,$6)`, [p, uid, kind, amount, note, cat]);
const approve = async s => as(admin, `select payroll_mark_paid($1)`, [(await period(s)).period_id]);

const admin = await user('admin', 'Sys Admin');
const mwe = await user('employee', 'Desk Minwage');
const contractor = await user('coach', 'Coach Contract');
const legacy = await user('employee', 'Desk Legacy');
const big = await user('employee', 'Desk Thirteenth');
const dm = await user('employee', 'Desk Deminimis');
const member = await user('member', 'Member Ben');
for (const u of [mwe, contractor, legacy, dm]) await as(admin, `select set_staff_salary($1, 13000, 'fixed')`, [u]);
await as(admin, `select set_staff_salary($1, 20000, 'fixed')`, [big]);

try {
  await test('Review Focus 7: staff, members and anon cannot reopen, mark paid out or switch', async () => {
    for (const who of [mwe, member]) {
      await assert.rejects(as(who, `select payroll_reopen(1, 'x')`), /forbidden/);
      await assert.rejects(as(who, `select payroll_mark_paid_out(1)`), /forbidden/);
      await assert.rejects(as(who, `select set_payroll_deductions($1, true, false, null)`, [mwe]), /forbidden/);
    }
  });

  await test('Review Focus 1: tax off / contributions on (minimum wage earner), and the other way round', async () => {
    await as(admin, `select set_payroll_deductions($1, true, false, 'Minimum wage earner')`, [mwe]);
    await as(admin, `select set_payroll_deductions($1, false, true, 'Contractor')`, [contractor]);
    const m = await row('2026-10-16', mwe);
    assert.equal(m.contributions_on, true); assert.equal(m.tax_on, false); assert.equal(m.deductions_reason, 'Minimum wage earner');
    assert.equal(n(m.sss), 1300); assert.equal(n(m.philhealth), 650); assert.equal(n(m.pagibig), 200); assert.equal(n(m.tax), 0);
    const c = await row('2026-10-16', contractor);
    assert.equal(n(c.sss) + n(c.philhealth) + n(c.pagibig), 0); assert.equal(n(c.tax), 387.45);   // taxable 13,000
    const s = (await as(admin, `select * from payroll_staff()`)).find(x => x.user_id === mwe);
    assert.equal(s.contributions_on, true); assert.equal(s.tax_on, false); assert.equal(s.deductions_reason, 'Minimum wage earner');
  });

  await test('Review Focus 1: the released app\'s one switch turns both off (as 0144 did)', async () => {
    await as(admin, `select set_payroll_deductions($1, false)`, [legacy]);
    const l = await row('2026-10-16', legacy);
    assert.equal(l.contributions_on, false); assert.equal(l.tax_on, false); assert.equal(n(l.gov_total), 0);
    await as(admin, `select set_payroll_deductions($1, true)`, [legacy]);
    assert.equal((await row('2026-10-16', legacy)).tax_on, true);
  });

  await test('Review Focus 2: 13th month is tax-free up to 90,000 a year, counting approved periods only', async () => {
    const jun = (await period('2025-06-01')).period_id;
    await line(jun, big, 'bonus', 60000, '13th month advance', 'thirteenth_month');
    await approve('2025-06-01');
    const may = (await period('2025-05-16')).period_id;   // a draft earlier in the year: not counted
    await line(may, big, 'bonus', 50000, 'draft only', 'thirteenth_month');
    const dec = (await period('2025-12-01')).period_id;
    await line(dec, big, 'bonus', 40000, '13th month balance', 'thirteenth_month');
    const r = await row('2025-12-01', big);
    assert.equal(n(r.nontaxable), 30000); assert.equal(n(r.thirteenth_used), 100000);
    assert.equal(n(r.taxable), 30000);                     // 60,000 gross - 30,000 non-taxable
    assert.equal(n(r.tax), 3604.10);                       // 937.50 + 20% x (30,000 - 16,667)
    const jan = (await period('2026-01-01')).period_id;    // new year: back to 0
    await line(jan, big, 'bonus', 50000, 'new year', 'thirteenth_month');
    assert.equal(n((await row('2026-01-01', big)).nontaxable), 50000);
  });

  await test('Review Focus 3: de minimis never taxed nor in the SSS / Pag-IBIG base; retro is', async () => {
    const o1 = (await period('2026-10-01')).period_id, o2 = (await period('2026-10-16')).period_id;
    await line(o1, dm, 'bonus', 2000, 'Rice subsidy', 'de_minimis');
    let a = await row('2026-10-01', dm);
    assert.equal(n(a.nontaxable), 2000); assert.equal(n(a.taxable), 13000); assert.equal(n(a.tax), 387.45);
    let b = await row('2026-10-16', dm);
    assert.equal(n(b.month_gross), 26000); assert.equal(n(b.msc), 26000);
    await line(o2, dm, 'bonus', 1000, 'September shift missed', 'retro');
    b = await row('2026-10-16', dm);
    assert.equal(n(b.month_gross), 27000); assert.equal(n(b.msc), 27000);
    await assert.rejects(line(o2, dm, 'deduction', 100, 'x', 'de_minimis'), /bad_input/);   // de minimis and 13th month are bonuses only
    await assert.rejects(line(o2, dm, 'deduction', 100, 'x', 'thirteenth_month'), /bad_input/);
    await line(o2, dm, 'deduction', 100, 'Overpaid in September', 'retro');
  });

  await test('Review Focus 4: Approve, Reopen with a reason, Approve again: fresh frozen lines, no duplicates', async () => {
    const sep = (await period('2026-09-01')).period_id;
    await line(sep, dm, 'bonus', 300, 'Holiday cover', 'holiday');
    await approve('2026-09-01');
    assert.equal((await period('2026-09-01')).status, 'approved');
    await assert.rejects(as(admin, `select payroll_reopen($1, ' ')`, [sep]), /bad_input/);
    await as(admin, `select payroll_reopen($1, 'Wrong salary for Desk Deminimis')`, [sep]);
    const p = await period('2026-09-01');
    assert.equal(p.status, 'draft'); assert.equal(p.paid_at, null);
    const salaryLines = async () => (await db.query(`select count(*)::int c from payroll_lines where period_id=$1 and kind='salary'`, [sep])).rows[0].c;
    assert.equal(await salaryLines(), 0);
    assert.equal((await as(admin, `select * from payroll_lines_for($1,$2)`, [sep, dm])).filter(l => l.category === 'holiday').length, 1);
    await as(admin, `select set_staff_salary($1, 14000, 'fixed')`, [dm]);
    await approve('2026-09-01');
    const people = (await period('2026-09-01')).rows.filter(r => !r.no_salary).length;
    assert.equal(await salaryLines(), people);
    assert.equal(n((await row('2026-09-01', dm)).base_pay), 14000);
    const acts = (await db.query(`select details from audit_log where action = 'payroll.reopened'`)).rows;
    assert.equal(acts[0].details.reason, 'Wrong salary for Desk Deminimis');
  });

  await test('Review Focus 4: a 1st-15th cannot be reopened while its 16th-end is approved', async () => {
    await approve('2026-09-16');
    await assert.rejects(as(admin, `select payroll_reopen($1, 'x')`, [(await period('2026-09-01')).period_id]), /second_half_approved/);
  });

  await test('Review Focus 5: Mark as paid out only after Approve; then nothing changes, not even Reopen', async () => {
    const draft = (await period('2026-08-16')).period_id;
    await assert.rejects(as(admin, `select payroll_mark_paid_out($1)`, [draft]), /not_approved/);
    const s16 = (await period('2026-09-16')).period_id;
    await as(admin, `select payroll_mark_paid_out($1)`, [s16]);
    const p = await period('2026-09-16');
    assert.equal(p.status, 'paid_out'); assert.ok(p.paid_out_at); assert.equal(p.paid_out_by_name, 'Sys Admin');
    await assert.rejects(as(admin, `select payroll_reopen($1, 'late fix')`, [s16]), /paid_out/);
    await assert.rejects(as(admin, `select payroll_mark_paid_out($1)`, [s16]), /paid_out/);
    await assert.rejects(line(s16, dm, 'bonus', 5, 'x', 'retro'), /payroll_paid/);
    assert.ok((await db.query(`select 1 from audit_log where action = 'payroll.paid_out'`)).rows.length);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nPayroll exemptions: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
