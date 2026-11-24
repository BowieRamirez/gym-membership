// Government deductions on payroll (plan 2026-11-24-payroll-deductions.md, T1-T12): SSS, PhilHealth, Pag-IBIG once a month in the
// 16th-end pay on the month's pay; BIR withholding tax every pay; switch per person; never below zero; frozen on paid.
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
async function asAnon(sql) {
  await db.query('begin');
  try { await db.query('set local role anon'); const r = await db.query(sql); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`pd-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const n = v => Number(v);
const one = async sql => n((await db.query(sql)).rows[0].v);
const period = async s => (await as(admin, `select payroll_period($1) p`, [s]))[0].p;
const row = async (s, uid) => (await period(s)).rows.find(r => r.user_id === uid);

const admin = await user('admin', 'Sys Admin');
const desk = await user('employee', 'Desk Ana');      // fixed 13,000 per period
const low = await user('coach', 'Coach Low');         // hourly 100, almost no hours
const off = await user('employee', 'Desk Off');       // deductions off
const member = await user('member', 'Member Ben');

try {
  await test('Review Focus 1: bracket edges', async () => {
    assert.equal(await one(`select public.payroll_tax(10417) v`), 0);
    assert.equal(await one(`select public.payroll_tax(10417.01) v`), 0);
    assert.equal(await one(`select public.payroll_tax(16667) v`), 937.5);
    assert.equal(await one(`select public.payroll_tax(20000) v`), 937.5 + 0.2 * 3333);
    assert.equal(await one(`select public.payroll_tax(400000) v`), 91770.7 + 0.35 * 66667);
    assert.equal(await one(`select public.payroll_sss(5249.99) v`), 250);
    assert.equal(await one(`select public.payroll_sss(5250) v`), 275);
    assert.equal(await one(`select public.payroll_sss(34750) v`), 1750);
    assert.equal(await one(`select public.payroll_sss(90000) v`), 1750);
    assert.equal(await one(`select public.payroll_philhealth(8000) v`), 250);
    assert.equal(await one(`select public.payroll_philhealth(26000) v`), 650);
    assert.equal(await one(`select public.payroll_philhealth(120000) v`), 2500);
    assert.equal(await one(`select public.payroll_pagibig(1500) v`), 15);
    assert.equal(await one(`select public.payroll_pagibig(1500.01) v`), 30);
    assert.equal(await one(`select public.payroll_pagibig(20000) v`), 200);
  });

  await test('Review Focus 9: staff, members and anon cannot read rates or switches, or switch', async () => {
    for (const who of [desk, low, member]) {
      await assert.rejects(as(who, `select set_payroll_deductions($1, false)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select payroll_deduction_rates_info()`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_deduction_rates`), /permission denied/);
      await assert.rejects(as(who, `select * from payroll_no_deductions`), /permission denied/);
    }
    await assert.rejects(asAnon(`select * from payroll_deduction_rates`), /permission denied/);
    await assert.rejects(as(admin, `select public.payroll_tax(1)`), /permission denied/);
    const info = (await as(admin, `select payroll_deduction_rates_info() r`))[0].r;
    assert.equal(n(info.sss_rate), 0.05);
  });

  await as(admin, `select set_staff_salary($1, 13000, 'fixed')`, [desk]);
  await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [low]);
  await as(admin, `select set_staff_salary($1, 13000, 'fixed')`, [off]);

  await test('Review Focus 2: 1st-15th has tax only; 16th-end has the month\'s contributions and its own tax', async () => {
    const a = await row('2026-10-01', desk);
    assert.equal(a.deductions_on, true);
    assert.equal(n(a.sss) + n(a.philhealth) + n(a.pagibig), 0);
    assert.equal(n(a.tax), 387.45);                      // (13,000 - 10,417) x 15%
    assert.equal(n(a.net), 13000 - 387.45);
    const b = await row('2026-10-16', desk);             // month gross 26,000
    assert.equal(n(b.sss), 1300); assert.equal(n(b.philhealth), 650); assert.equal(n(b.pagibig), 200);
    assert.equal(n(b.tax), 64.95);                       // taxable 13,000 - 2,150 = 10,850
    assert.equal(n(b.gov_total), 2214.95);
    assert.equal(n(b.deductions), 2214.95);              // Review Focus 8: deductions and net include them
    assert.equal(n(b.net), 13000 - 2214.95);
    assert.equal(n((await period('2026-10-16')).total_gov) >= 2214.95, true);
  });

  await test('Review Focus 3: the 16th-end follows the 1st-15th while it is a draft', async () => {
    const p1 = (await period('2026-10-01')).period_id;
    await db.query(`insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted)
                    values ($1, '2026-10-02 08:00+08', '2026-10-02 10:00+08', 0,0,0,0,0)`, [low]);
    let b = await row('2026-10-16', low);
    const before = n(b.month_gross);
    // coaches are paid from check-ins; give the hourly coach a 2 h check-in in the 1st half and approve it
    const v = (await db.query(`insert into coach_visits(coach_id, checked_in_by, checked_in_at, checked_out_at)
                               values ($1,$1,'2026-10-03 09:00+08','2026-10-03 11:00+08') returning coach_visit_id`, [low])).rows[0].coach_visit_id;
    await as(admin, `select payroll_review_time($1,'coach_visit',$2,'approved',null)`, [p1, v]);
    b = await row('2026-10-16', low);
    assert.equal(n(b.month_gross), before + 200, 'month pay grew with the approved 1st-half hours');
  });

  await test('Review Focus 6: low pay: government deductions cut so pay stays at or above zero', async () => {
    const b = await row('2026-10-16', low);              // month gross 200, own gross 0
    assert.equal(n(b.net), 0); assert.equal(b.gov_cut, true);
    assert.equal(n(b.gov_total), 0);
    const a = await row('2026-10-01', low);              // own gross 200: no tax, no contributions in the 1st half
    assert.equal(n(a.gov_total), 0); assert.equal(a.gov_cut, false); assert.equal(n(a.net), 200);
  });

  await test('review fix: someone with no pay all month gets no "cut" warning; MSC and Pag-IBIG base come from the table', async () => {
    const idle = await user('coach', 'Coach Idle');
    await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [idle]);
    const r = await row('2026-10-16', idle);
    assert.equal(r.gov_cut, false); assert.equal(n(r.gov_total), 0);
    const d = await row('2026-10-16', desk);
    assert.equal(n(d.msc), 26000); assert.equal(n(d.pagibig_base), 10000);
    await db.query(`update payroll_deduction_rates set pagibig_cap = 5000`);
    assert.equal(n((await row('2026-10-16', desk)).pagibig_base), 5000);
    assert.equal(await one(`select public.payroll_pagibig(20000) v`), 100);
    await db.query(`update payroll_deduction_rates set pagibig_cap = 10000`);
  });

  await test('review fix: the switch is refused for a deactivated account', async () => {
    const gone = await user('employee', 'Desk Gone');
    await as(admin, `select set_user_active($1, false)`, [gone]);
    await assert.rejects(as(admin, `select set_payroll_deductions($1, false)`, [gone]), /bad_input/);
  });

  await test('Review Focus 5: switch off: no government deductions; own switch refused; audited', async () => {
    await as(admin, `select set_payroll_deductions($1, false)`, [off]);
    const r = await row('2026-10-16', off);
    assert.equal(r.deductions_on, false); assert.equal(n(r.gov_total), 0); assert.equal(n(r.net), 13000);
    await assert.rejects(as(admin, `select set_payroll_deductions($1, false)`, [admin]), /own_salary/);
    const s = (await as(admin, `select * from payroll_staff()`)).find(x => x.user_id === off);
    assert.equal(s.deductions_on, false);
    const acts = (await db.query(`select action from audit_log where action = 'payroll.deductions_set'`)).rows;
    assert.ok(acts.length >= 1);
  });

  await test('Review Focus 7 / T6: manual lines cannot use the government categories', async () => {
    const p = (await period('2026-10-16')).period_id;
    for (const c of ['sss', 'philhealth', 'pagibig', 'tax'])
      await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'deduction',10,'x',$3)`, [p, desk, c]), /bad_input/);
  });

  await test('Review Focus 4: the 16th-end cannot be paid before the 1st-15th', async () => {
    const p2 = (await period('2026-10-16')).period_id;
    await assert.rejects(as(admin, `select payroll_mark_paid($1)`, [p2]), /first_half_unpaid/);
  });

  await test('Review Focus 7: paid periods freeze the government lines; later changes change nothing', async () => {
    const p1 = (await period('2026-10-01')).period_id, p2 = (await period('2026-10-16')).period_id;
    await as(admin, `select payroll_approve_all($1, null)`, [p1]);
    await as(admin, `select payroll_mark_paid($1)`, [p1]);
    await as(admin, `select payroll_approve_all($1, null)`, [p2]);
    await as(admin, `select payroll_mark_paid($1)`, [p2]);
    const lines = await as(admin, `select * from payroll_lines_for($1,$2)`, [p2, desk]);
    const by = c => n(lines.find(l => l.category === c)?.amount ?? 0);
    assert.equal(by('sss'), 1300); assert.equal(by('philhealth'), 650); assert.equal(by('pagibig'), 200); assert.equal(by('tax'), 64.95);
    assert.match(lines.find(l => l.category === 'sss').note, /SSS.*Oct 2026.*13,000|SSS.*Oct 2026.*26,000/);
    assert.equal((await as(admin, `select * from payroll_lines_for($1,$2)`, [p2, off])).filter(l => ['sss', 'philhealth', 'pagibig', 'tax'].includes(l.category)).length, 0);
    await db.query(`update payroll_deduction_rates set sss_rate = 0.10`);
    await as(admin, `select set_staff_salary($1, 50000, 'fixed')`, [desk]);
    await as(admin, `select set_payroll_deductions($1, false)`, [desk]);
    const r = await row('2026-10-16', desk);
    assert.equal(n(r.sss), 1300); assert.equal(n(r.tax), 64.95); assert.equal(n(r.net), 13000 - 2214.95);
    assert.equal(n((await row('2026-10-01', desk)).tax), 387.45);
    await db.query(`update payroll_deduction_rates set sss_rate = 0.05`);
  });

  await test('T9: periods paid before deductions existed show none', async () => {
    const pid = (await db.query(`insert into payroll_periods(starts_on, ends_on, paid_at, paid_by) values ('2025-06-16','2025-06-30',now(),$1) returning period_id`, [admin])).rows[0].period_id;
    await db.query(`insert into payroll_lines(period_id,user_id,kind,amount,created_by) values ($1,$2,'salary',13000,$3)`, [pid, off, admin]);
    const r = await row('2025-06-16', off);
    assert.equal(n(r.gov_total), 0); assert.equal(n(r.net), 13000);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nPayroll deductions: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
