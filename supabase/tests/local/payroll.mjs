// Phase 17 (P1-P10): Payroll database — salaries, 1st-15th / 16th-end periods, bonus/deduction lines, mark paid, audit.
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
  try {
    await db.query('set local role anon');
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`pr-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const admin = await user('admin', 'Sys Admin');
const sub = await user('subadmin', 'Sub Admin');
const desk = await user('employee', 'Desk Dana');
const deskNoSalary = await user('employee', 'Desk Leo');
const coach = await user('coach', 'Coach Kai');
const coachLeaving = await user('coach', 'Coach Mia');
const member = await user('member', 'Member Ben');

try {
  await test('Review Focus 1: front desk, coach, member, and anon cannot call payroll functions or read tables', async () => {
    for (const who of [desk, coach, member]) {
      await assert.rejects(as(who, `select * from payroll_staff()`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_period('2026-10-01')`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_lines_for(1, $1)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select * from set_staff_salary($1, 5000)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select * from staff_salaries`), /permission denied/);
      await assert.rejects(as(who, `select * from payroll_periods`), /permission denied/);
      await assert.rejects(as(who, `select * from payroll_lines`), /permission denied/);
    }
    await assert.rejects(asAnon(`select * from payroll_staff()`), /permission denied|forbidden/);
    await assert.rejects(asAnon(`select * from staff_salaries`), /permission denied/);
  });

  await test('period start must be the 1st or 16th of a month', async () => {
    await assert.rejects(as(admin, `select * from payroll_period('2026-10-02')`), /bad_input/);
    await assert.rejects(as(admin, `select * from payroll_period('2026-10-15')`), /bad_input/);
    const p = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    assert.equal(p.starts_on, '2026-10-01');
    assert.equal(p.ends_on, '2026-10-15');
    const p2 = (await as(admin, `select payroll_period('2026-02-16') p`))[0].p;
    assert.equal(p2.ends_on, '2026-02-28');
  });

  await test('salaries: own_salary refused; admins appear only once a salary is set; no_salary shown for unset desk/coach', async () => {
    await assert.rejects(as(admin, `select set_staff_salary($1, 12000)`, [admin]), /own_salary/);
    await assert.rejects(as(sub, `select set_staff_salary($1, 12000)`, [sub]), /own_salary/);
    await as(admin, `select set_staff_salary($1, 8000)`, [desk]);
    await as(admin, `select set_staff_salary($1, 10000)`, [coach]);
    await as(admin, `select set_staff_salary($1, 9000)`, [coachLeaving]);
    await as(admin, `select set_staff_salary($1, 15000)`, [sub]);

    const staff = await as(admin, `select * from payroll_staff()`);
    const names = staff.map(r => r.full_name);
    assert.ok(names.includes('Desk Dana'));
    assert.ok(names.includes('Desk Leo'));
    assert.ok(names.includes('Coach Kai'));
    assert.ok(names.includes('Sub Admin'));
    assert.ok(!names.includes('Sys Admin'));
    assert.ok(!names.includes('Member Ben'));

    const p = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    const leo = p.rows.find(r => r.user_id === deskNoSalary);
    assert.equal(leo.no_salary, true);
    assert.equal(Number(leo.salary), 0);
  });

  await test('desk hours = closed shifts only; coach hours match coach_hours_report for the same dates', async () => {
    // Open & close a 3-hour shift for Desk Dana on 2026-10-05 Manila, plus an unclosed shift
    await db.query(
      `insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted)
       values ($1, '2026-10-05T08:00:00+08:00', '2026-10-05T11:00:00+08:00', 5000, 0, 0, 5000, 5000),
              ($1, '2026-10-06T08:00:00+08:00', null, 5000, null, null, null, null)`,
      [desk]
    );
    // 1 completed 2h session and 1 no-show 1h session for Coach Kai on 2026-10-05 Manila
    await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
       values ($1, $2, '2026-10-05T09:00:00+08:00', '2026-10-05T11:00:00+08:00', 'completed'),
              ($1, $2, '2026-10-05T13:00:00+08:00', '2026-10-05T14:00:00+08:00', 'no_show')`,
      [coach, member]
    );
    const report = (await as(admin, `select * from coach_hours_report('2026-10-01', '2026-10-15')`))
      .find(r => r.coach_id === coach);
    const p = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    const danaRow = p.rows.find(r => r.user_id === desk);
    const kaiRow = p.rows.find(r => r.user_id === coach);
    assert.equal(Number(danaRow.hours), 3);
    assert.equal(Number(kaiRow.hours), Number(report.completed_hours));
    assert.equal(kaiRow.sessions_count, report.completed_sessions);
  });

  let periodId;
  await test('bonus/deduction require a note; Review Focus 2: salary changes update draft; Review Focus 4: net_below_zero refuses Mark as paid', async () => {
    const p = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    periodId = p.period_id;

    await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'bonus',500,'')`, [periodId, desk]), /bad_input/);
    await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'deduction',0,'x')`, [periodId, desk]), /bad_input/);

    const bonus = (await as(admin, `select * from payroll_add_line($1,$2,'bonus',500,'Holiday cover')`, [periodId, desk]))[0];
    const bigDed = (await as(admin, `select * from payroll_add_line($1,$2,'deduction',9000,'Too big')`, [periodId, desk]))[0];
    await assert.rejects(as(admin, `select payroll_mark_paid($1)`, [periodId]), /net_below_zero:Desk Dana/);

    await as(admin, `select payroll_remove_line($1)`, [bigDed.line_id]);
    await as(admin, `select set_staff_salary($1, 8500)`, [desk]);

    const updated = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    const dana = updated.rows.find(r => r.user_id === desk);
    assert.equal(Number(dana.salary), 8500);
    assert.equal(Number(dana.bonuses), 500);
    assert.equal(Number(dana.net), 9000);
    assert.ok(bonus.line_id > 0);
  });

  await test('Mark as paid before period ends -> period_not_ended', async () => {
    const todayManila = (await db.query(`select (now() at time zone 'Asia/Manila')::date d`)).rows[0].d;
    const y = todayManila.getUTCFullYear();
    const m = String(todayManila.getUTCMonth() + 1).padStart(2, '0');
    const day = todayManila.getUTCDate();
    // Pick a future period start so ends_on is definitely > todayManila
    const futureStart = `${y + 1}-01-01`;
    const fp = (await as(admin, `select payroll_period($1) p`, [futureStart]))[0].p;
    await assert.rejects(as(admin, `select payroll_mark_paid($1)`, [fp.period_id]), /period_not_ended/);
  });

  await test('Review Focus 3, 5, 6 & P10: Mark as paid locks period, freezes salaries, ignores later salary/active changes, leaves revenue/shifts untouched', async () => {
    const revBefore = await as(admin, `select * from revenue_days('2026-10-01', '2026-10-15')`);
    const shiftsBefore = await as(admin, `select * from cash_shifts_list('2026-10-01', '2026-10-15')`);

    await as(admin, `select payroll_mark_paid($1)`, [periodId]);
    await assert.rejects(as(sub, `select payroll_mark_paid($1)`, [periodId]), /payroll_paid/);
    await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'bonus',100,'Late')`, [periodId, desk]), /payroll_paid/);

    const lines = await as(admin, `select * from payroll_lines_for($1,$2)`, [periodId, desk]);
    const bonusLine = lines.find(l => l.kind === 'bonus');
    await assert.rejects(as(admin, `select payroll_remove_line($1)`, [bonusLine.line_id]), /payroll_paid/);

    // Person with no salary was skipped (no salary line written)
    const leoLines = await as(admin, `select * from payroll_lines_for($1,$2)`, [periodId, deskNoSalary]);
    assert.equal(leoLines.length, 0);

    // Change Desk Dana's salary and deactivate Coach Mia (Review Focus 2 & 6)
    await as(admin, `select set_staff_salary($1, 9500)`, [desk]);
    await as(admin, `select set_user_active($1, false)`, [coachLeaving]);

    // Paid period keeps old salary (8500) and still shows Coach Mia
    const paidView = (await as(admin, `select payroll_period('2026-10-01') p`))[0].p;
    assert.ok(paidView.paid_at);
    assert.equal(Number(paidView.rows.find(r => r.user_id === desk).salary), 8500);
    assert.ok(paidView.rows.some(r => r.user_id === coachLeaving));

    // Next draft (16-31 Oct) shows new salary (9500) and drops deactivated Coach Mia
    const nextDraft = (await as(admin, `select payroll_period('2026-10-16') p`))[0].p;
    assert.equal(Number(nextDraft.rows.find(r => r.user_id === desk).salary), 9500);
    assert.ok(!nextDraft.rows.some(r => r.user_id === coachLeaving));

    // P10: Revenue and cash_shifts untouched
    const revAfter = await as(admin, `select * from revenue_days('2026-10-01', '2026-10-15')`);
    const shiftsAfter = await as(admin, `select * from cash_shifts_list('2026-10-01', '2026-10-15')`);
    assert.deepEqual(revAfter, revBefore);
    assert.deepEqual(shiftsAfter, shiftsBefore);

    // Audit rows written
    const audits = (await db.query(`select action from audit_log where action like 'payroll.%'`)).rows.map(r => r.action);
    assert.ok(audits.includes('payroll.salary_set'));
    assert.ok(audits.includes('payroll.line_added'));
    assert.ok(audits.includes('payroll.line_removed'));
    assert.ok(audits.includes('payroll.paid'));
  });

  await test('role salaries apply to all existing and new front desk / coaches; non-admins refused', async () => {
    await assert.rejects(as(desk, `select set_role_salary('employee', 7500)`), /forbidden/);
    await assert.rejects(as(admin, `select * from payroll_role_salaries()`), /forbidden|$/).catch(() => {}); // admin allowed below
    await assert.rejects(as(desk, `select * from payroll_role_salaries()`), /forbidden/);
    await assert.rejects(as(admin, `select set_role_salary('admin', 20000)`), /bad_input/);

    await as(admin, `select set_role_salary('employee', 7800)`);
    await as(admin, `select set_role_salary('coach', 11000)`);

    const roles = await as(admin, `select * from payroll_role_salaries()`);
    assert.equal(Number(roles.find(r => r.role === 'employee').amount), 7800);
    assert.equal(Number(roles.find(r => r.role === 'coach').amount), 11000);

    // Existing desk (including Desk Leo who had no salary) and coach get the role salary
    const staff = await as(admin, `select * from payroll_staff()`);
    assert.equal(Number(staff.find(s => s.user_id === desk).amount), 7800);
    assert.equal(Number(staff.find(s => s.user_id === deskNoSalary).amount), 7800);
    assert.equal(Number(staff.find(s => s.user_id === coach).amount), 11000);

    // A newly added front desk employee automatically inherits the role salary
    const newDesk = await user('employee', 'Desk New');
    const draft = (await as(admin, `select payroll_period('2026-11-01') p`))[0].p;
    const newRow = draft.rows.find(r => r.user_id === newDesk);
    assert.equal(newRow.no_salary, false);
    assert.equal(Number(newRow.salary), 7800);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nPayroll: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
