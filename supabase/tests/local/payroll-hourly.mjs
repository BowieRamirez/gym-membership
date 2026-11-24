// Hourly and per-session payroll (plan 2026-11-24-hourly-payroll.md, H1-H17): pay types, personal vs role rates,
// approved shifts / visits / sessions, overlaps counted once, corrections, categories, gross, frozen on paid.
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
    [`ph-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const period = async start => (await as(admin, `select payroll_period($1) p`, [start]))[0].p;
const row = async (start, uid) => (await period(start)).rows.find(r => r.user_id === uid);
const records = async (pid, uid) => as(admin, `select * from payroll_records_for($1,$2) order by starts_at`, [pid, uid]);
async function shift(uid, from, to) {
  return (await db.query(`insert into cash_shifts(cashier_id, opened_at, closed_at, opening_float, cash_in, cash_out, expected, counted)
    values ($1,$2,$3,0, case when $3::timestamptz is null then null else 0 end, case when $3::timestamptz is null then null else 0 end,
            case when $3::timestamptz is null then null else 0 end, case when $3::timestamptz is null then null else 0 end) returning shift_id`,
    [uid, from, to])).rows[0].shift_id;
}
async function visit(uid, from, to, auto = false) {
  return (await db.query(`insert into coach_visits(coach_id, checked_in_by, checked_in_at, checked_out_at, auto_closed)
    values ($1,$1,$2,$3,$4) returning coach_visit_id`, [uid, from, to, auto])).rows[0].coach_visit_id;
}
async function session(coach, member, from, to, status = 'completed') {
  return (await db.query(`insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
    values ($1,$2,$3,$4,$5) returning training_session_id`, [coach, member, from, to, status])).rows[0].training_session_id;
}
const review = (pid, src, id, status, reason = null) =>
  as(admin, `select payroll_review_time($1,$2,$3,$4,$5)`, [pid, src, id, status, reason]);

const admin = await user('admin', 'Sys Admin');
const desk = await user('employee', 'Desk Ana');        // follows the role
const desk2 = await user('employee', 'Desk Bo');        // personal rate
const desk3 = await user('employee', 'Desk Cy');
const coachH = await user('coach', 'Coach Hour');
const coachS = await user('coach', 'Coach Sess');
const member = await user('member', 'Member Ben');

try {
  await test('RF1: non-admins and anon cannot use the new functions or read the new tables', async () => {
    for (const who of [desk, coachH, member]) {
      await assert.rejects(as(who, `select * from payroll_records_for(1,$1)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select payroll_review_time(1,'cash_shift',1,'approved',null)`), /forbidden/);
      await assert.rejects(as(who, `select payroll_approve_all(1,null)`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_add_hours(1,$1,1,'x')`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select payroll_remove_hours(1)`), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_adjustments_for(1,$1)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select use_role_salary($1)`, [desk]), /forbidden/);
      await assert.rejects(as(who, `select * from payroll_hour_adjustments`), /permission denied/);
      await assert.rejects(as(who, `select * from payroll_time_reviews`), /permission denied/);
    }
    await assert.rejects(asAnon(`select * from payroll_records_for(1,null)`), /permission denied|forbidden/);
    await assert.rejects(asAnon(`select * from payroll_time_reviews`), /permission denied/);
    await assert.rejects(as(admin, `select * from payroll_basis(1)`), /permission denied/);
  });

  await test('H1: salaries default to Fixed; Per session only for coaches', async () => {
    await as(admin, `select set_staff_salary($1, 8000)`, [desk3]);
    const s = (await as(admin, `select * from payroll_staff()`)).find(r => r.user_id === desk3);
    assert.equal(s.pay_type, 'fixed');
    assert.equal(s.personal, true);
    await assert.rejects(as(admin, `select set_staff_salary($1, 150, 'per_session')`, [desk3]), /per_session_coach_only/);
    await assert.rejects(as(admin, `select set_role_salary('employee', 150, 'per_session')`), /per_session_coach_only/);
    await assert.rejects(as(admin, `select set_staff_salary($1, 150, 'weekly')`, [desk3]), /bad_input/);
    // leaving the type out keeps the current one (the released app does not send it)
    await as(admin, `select set_staff_salary($1, 90, 'hourly')`, [desk3]);
    await as(admin, `select set_staff_salary($1, 95)`, [desk3]);
    const s2 = (await as(admin, `select * from payroll_staff()`)).find(r => r.user_id === desk3);
    assert.equal(s2.pay_type, 'hourly'); assert.equal(Number(s2.amount), 95);
  });

  await test('RF13 / H14: a personal rate survives a role default change; Use role default goes back', async () => {
    await as(admin, `select set_role_salary('employee', 80, 'hourly')`);
    await as(admin, `select set_staff_salary($1, 90, 'hourly')`, [desk2]);
    await as(admin, `select set_role_salary('employee', 85, 'hourly')`);
    let staff = await as(admin, `select * from payroll_staff()`);
    const ana = staff.find(r => r.user_id === desk); let bo = staff.find(r => r.user_id === desk2);
    assert.equal(Number(ana.amount), 85); assert.equal(ana.pay_type, 'hourly'); assert.equal(ana.personal, false);
    assert.equal(Number(bo.amount), 90); assert.equal(bo.personal, true);
    await as(admin, `select use_role_salary($1)`, [desk2]);
    bo = (await as(admin, `select * from payroll_staff()`)).find(r => r.user_id === desk2);
    assert.equal(Number(bo.amount), 85); assert.equal(bo.personal, false);
    await as(admin, `select set_staff_salary($1, 90, 'hourly')`, [desk2]);   // personal again for the overlap test
    await assert.rejects(as(admin, `select use_role_salary($1)`, [admin]), /bad_input/);
    const roles = await as(admin, `select * from payroll_role_salaries()`);
    assert.equal(roles.find(r => r.role === 'employee').pay_type, 'hourly');
  });

  let sep, s1, s2;
  await test('RF2 / RF8 / RF12 / H12: only approved closed shifts count; open shift flagged; reject needs a reason', async () => {
    sep = (await period('2026-09-01')).period_id;
    s1 = await shift(desk, '2026-09-01T08:00:00+08:00', '2026-09-01T12:00:00+08:00');   // 4 h
    s2 = await shift(desk, '2026-09-02T08:00:00+08:00', '2026-09-02T11:30:00+08:00');   // 3.5 h
    await shift(desk, '2026-09-03T08:00:00+08:00', null);                               // still open
    let recs = await records(sep, desk);
    assert.equal(recs.length, 3);
    assert.deepEqual(recs.map(r => r.status), ['pending', 'pending', 'pending']);
    assert.ok(recs[2].flags.includes('open'));
    let r = await row('2026-09-01', desk);
    assert.equal(r.pay_type, 'hourly'); assert.equal(Number(r.rate), 85);
    assert.equal(Number(r.logged), 0); assert.equal(r.pending, 2);
    await review(sep, 'cash_shift', s1, 'approved');
    await assert.rejects(review(sep, 'cash_shift', s2, 'rejected'), /bad_input/);
    await review(sep, 'cash_shift', s2, 'rejected', 'Opened by mistake');
    r = await row('2026-09-01', desk);
    assert.equal(Number(r.logged), 4); assert.equal(r.pending, 0); assert.equal(Number(r.base_pay), 340);
    recs = await records(sep, desk);
    assert.equal(recs[1].status, 'rejected'); assert.equal(recs[1].reason, 'Opened by mistake');
    await review(sep, 'cash_shift', s2, 'approved');
    r = await row('2026-09-01', desk);
    assert.equal(Number(r.logged), 7.5); assert.equal(Number(r.base_pay), 637.5);
    await review(sep, 'cash_shift', s2, 'pending');
    assert.equal((await row('2026-09-01', desk)).pending, 1);
    await review(sep, 'cash_shift', s2, 'approved');
    // an open shift cannot be approved
    const open = (await records(sep, desk))[2];
    await assert.rejects(review(sep, 'cash_shift', open.source_id, 'approved'), /record_open/);
  });

  await test('RF11 / H13: overlapping approved time counts once and is flagged', async () => {
    const aug = (await period('2026-08-01')).period_id;
    const a = await shift(desk2, '2026-08-03T08:00:00+08:00', '2026-08-03T12:00:00+08:00');
    const b = await shift(desk2, '2026-08-03T11:00:00+08:00', '2026-08-03T14:00:00+08:00');
    const c = await shift(desk2, '2026-08-03T09:00:00+08:00', '2026-08-03T10:00:00+08:00');
    const recs = await records(aug, desk2);
    assert.ok(recs.every(x => x.flags.includes('overlap')));
    for (const id of [a, b, c]) await review(aug, 'cash_shift', id, 'approved');
    assert.equal(Number((await row('2026-08-01', desk2)).logged), 6);
  });

  await test('RF9: Approve all approves only unflagged pending records', async () => {
    const aug = (await period('2026-08-01')).period_id;
    await shift(desk3, '2026-08-04T08:00:00+08:00', '2026-08-04T10:00:00+08:00');
    await shift(desk3, '2026-08-05T06:00:00+08:00', '2026-08-05T19:30:00+08:00');   // 13.5 h → long
    const n = (await as(admin, `select payroll_approve_all($1,$2) n`, [aug, desk3]))[0].n;
    assert.equal(n, 1);
    const recs = await records(aug, desk3);
    assert.deepEqual(recs.map(r => r.status), ['approved', 'pending']);
    assert.ok(recs[1].flags.includes('long'));
  });

  await test('H3: hourly coach is paid by approved check-in/out; auto-closed visits are flagged', async () => {
    await as(admin, `select set_staff_salary($1, 100, 'hourly')`, [coachH]);
    const aug = (await period('2026-08-01')).period_id;
    await visit(coachH, '2026-08-06T09:00:00+08:00', '2026-08-06T11:00:00+08:00');
    const auto = await visit(coachH, '2026-08-07T18:00:00+08:00', '2026-08-07T21:00:00+08:00', true);
    await session(coachH, member, '2026-08-06T09:00:00+08:00', '2026-08-06T10:00:00+08:00');   // ignored for hourly pay
    assert.equal(await as(admin, `select payroll_approve_all($1,null) n`, [aug]).then(r => r[0].n) >= 1, true);
    let r = await row('2026-08-01', coachH);
    assert.equal(Number(r.logged), 2); assert.equal(r.flagged, 1);
    const recs = await records(aug, coachH);
    assert.equal(recs.length, 2);
    assert.ok(recs.find(x => x.source_id == auto).flags.includes('auto_closed'));
    await review(aug, 'coach_visit', auto, 'approved');
    r = await row('2026-08-01', coachH);
    assert.equal(Number(r.logged), 5); assert.equal(Number(r.base_pay), 500);
  });

  await test('RF5 / RF14 / H15: per-session coach is paid by approved completed sessions only', async () => {
    await as(admin, `select set_staff_salary($1, 150, 'per_session')`, [coachS]);
    const aug = (await period('2026-08-01')).period_id;
    const x1 = await session(coachS, member, '2026-08-08T09:00:00+08:00', '2026-08-08T10:00:00+08:00');
    const x2 = await session(coachS, member, '2026-08-08T11:00:00+08:00', '2026-08-08T12:00:00+08:00');
    const x3 = await session(coachS, member, '2026-08-08T13:00:00+08:00', '2026-08-08T14:00:00+08:00');
    await session(coachS, member, '2026-08-08T15:00:00+08:00', '2026-08-08T16:00:00+08:00', 'no_show');
    await session(coachS, member, '2026-08-08T16:00:00+08:00', '2026-08-08T17:00:00+08:00', 'cancelled');
    await visit(coachS, '2026-08-08T08:00:00+08:00', '2026-08-08T18:00:00+08:00');   // ignored for per-session pay
    const recs = await records(aug, coachS);
    assert.equal(recs.length, 3);
    assert.ok(recs.every(x => x.source === 'training_session'));
    await review(aug, 'training_session', x1, 'approved');
    await review(aug, 'training_session', x2, 'approved');
    let r = await row('2026-08-01', coachS);
    assert.equal(Number(r.logged), 2); assert.equal(r.pending, 1); assert.equal(Number(r.base_pay), 300);
    await review(aug, 'training_session', x3, 'rejected', 'Member left early');
    r = await row('2026-08-01', coachS);
    assert.equal(Number(r.logged), 2); assert.equal(r.pending, 0);
  });

  await test('RF4 / H5: corrections need a note, show in the row, never take pay below zero; not for Fixed pay', async () => {
    const aug = (await period('2026-08-01')).period_id;
    await assert.rejects(as(admin, `select * from payroll_add_hours($1,$2,1,'')`, [aug, desk3]), /bad_input/);
    await assert.rejects(as(admin, `select * from payroll_add_hours($1,$2,0,'x')`, [aug, desk3]), /bad_input/);
    const adj = (await as(admin, `select * from payroll_add_hours($1,$2,-10,'Wrong drawer')`, [aug, desk3]))[0];
    let r = await row('2026-08-01', desk3);
    assert.equal(Number(r.logged), 2); assert.equal(Number(r.adjustment), -10);
    assert.equal(Number(r.payable), 0); assert.equal(Number(r.base_pay), 0);
    const list = await as(admin, `select * from payroll_adjustments_for($1,$2)`, [aug, desk3]);
    assert.equal(list.length, 1); assert.equal(list[0].note, 'Wrong drawer');
    await as(admin, `select payroll_remove_hours($1)`, [adj.adjustment_id]);
    await as(admin, `select * from payroll_add_hours($1,$2,1.5,'Forgot to open the drawer')`, [aug, desk3]);
    r = await row('2026-08-01', desk3);
    assert.equal(Number(r.payable), 3.5); assert.equal(Number(r.base_pay), 332.5);
    const fixedCoach = await user('coach', 'Coach Fixed');
    await as(admin, `select set_staff_salary($1, 5000, 'fixed')`, [fixedCoach]);
    await assert.rejects(as(admin, `select * from payroll_add_hours($1,$2,1,'x')`, [aug, fixedCoach]), /bad_input/);
  });

  await test('RF15 / RF16 / H16 / H17: categories on bonus and deduction lines; gross = base + bonuses', async () => {
    const aug = (await period('2026-08-01')).period_id;
    await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'bonus',500,'x','birthday')`, [aug, coachH]), /bad_input/);
    await assert.rejects(as(admin, `select * from payroll_add_line($1,$2,'bonus',500,'x',null)`, [aug, coachH]), /bad_input/);
    await as(admin, `select * from payroll_add_line($1,$2,'bonus',500,'Sunday cover','overtime')`, [aug, coachH]);
    await as(admin, `select * from payroll_add_line($1,$2,'deduction',200,'Late','other')`, [aug, coachH]);
    const lines = await as(admin, `select * from payroll_lines_for($1,$2)`, [aug, coachH]);
    assert.deepEqual(lines.map(l => l.category).sort(), ['other', 'overtime']);
    const r = await row('2026-08-01', coachH);
    assert.equal(Number(r.base_pay), 500); assert.equal(Number(r.gross), 1000); assert.equal(Number(r.net), 800);
    const p = await period('2026-08-01');
    assert.equal(Number(p.total_gross), p.rows.reduce((s, x) => s + Number(x.gross), 0));
  });

  await test('RF8: Mark as paid refused while anyone has a pending record', async () => {
    const aug = (await period('2026-08-01')).period_id;
    await assert.rejects(as(admin, `select payroll_mark_paid($1)`, [aug]), /time_pending:Desk Cy/);
  });

  await test('RF3 / RF6 / RF10 / H7 / H11: paid period freezes type, quantity and rate; locked afterwards', async () => {
    const zero = await user('employee', 'Desk Zero');
    await as(admin, `select set_staff_salary($1, 70, 'hourly')`, [zero]);
    await as(admin, `select payroll_mark_paid($1)`, [sep]);
    const line = (await as(admin, `select * from payroll_lines_for($1,$2)`, [sep, desk])).find(l => l.kind === 'salary');
    assert.equal(Number(line.amount), 637.5);
    const frozen = (await db.query(`select pay_type, quantity, rate from payroll_lines where period_id=$1 and user_id=$2 and kind='salary'`, [sep, desk])).rows[0];
    assert.equal(frozen.pay_type, 'hourly'); assert.equal(Number(frozen.quantity), 7.5); assert.equal(Number(frozen.rate), 85);
    const z = await row('2026-09-01', zero);
    assert.ok(z, 'hourly person with 0 h is listed'); assert.equal(Number(z.base_pay), 0);
    await assert.rejects(review(sep, 'cash_shift', s1, 'pending'), /payroll_paid/);
    await assert.rejects(as(admin, `select * from payroll_add_hours($1,$2,1,'x')`, [sep, desk]), /payroll_paid/);
    await assert.rejects(as(admin, `select payroll_approve_all($1,null)`, [sep]), /payroll_paid/);
    await as(admin, `select set_role_salary('employee', 120, 'hourly')`);
    const after = await row('2026-09-01', desk);
    assert.equal(Number(after.rate), 85); assert.equal(Number(after.base_pay), 637.5); assert.equal(Number(after.payable), 7.5);
    // a record from another period cannot be reviewed under this one
    const aug = (await period('2026-08-01')).period_id;
    await assert.rejects(review(aug, 'cash_shift', s1, 'approved'), /not_found/);
  });

  await test('review fix: a paid period never shows pending records that can no longer be decided', async () => {
    await shift(desk, '2026-09-05T08:00:00+08:00', '2026-09-05T10:00:00+08:00');   // closed after Sep 1-15 was paid
    assert.equal((await row('2026-09-01', desk)).pending, 0);
  });

  await test('review fix: with hourly or per-session people, Mark as paid waits until the day after the period ends', async () => {
    const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date d`)).rows[0].d;
    // a period that ends today (inserted directly: the real one may end later this half-month)
    const pid = (await db.query(`insert into payroll_periods(starts_on, ends_on) values ('2000-01-01', $1) returning period_id`,
      [today])).rows[0].period_id;
    await assert.rejects(as(admin, `select payroll_mark_paid($1)`, [pid]), /period_not_ended/);
  });

  await test('0143: a missing status is bad_input, not a database error', async () => {
    const aug = (await period('2026-08-01')).period_id;
    await assert.rejects(review(aug, 'cash_shift', 1, null), /bad_input/);
  });

  await test('0143: Use role default on someone already following the role changes nothing and logs nothing', async () => {
    const follower = await user('employee', 'Desk Follower');
    const before = (await db.query(`select count(*)::int n from audit_log where entity_id = $1::text`, [follower])).rows[0].n;
    await as(admin, `select use_role_salary($1)`, [follower]);
    const after = (await db.query(`select count(*)::int n from audit_log where entity_id = $1::text`, [follower])).rows[0].n;
    assert.equal(after, before);
  });

  await test('0143: a paid period shows the approved hours it was paid for, even if records change later', async () => {
    const late = await shift(desk, '2026-09-06T08:00:00+08:00', '2026-09-06T10:00:00+08:00');
    await db.query(`insert into payroll_time_reviews(source, source_id, status, reviewed_by) values ('cash_shift', $1, 'approved', $2)`, [late, admin]);
    const r = await row('2026-09-01', desk);
    assert.equal(Number(r.logged), 7.5); assert.equal(Number(r.payable), 7.5);
  });

  await test('RF7: a Part 3 paid line (no type, quantity or rate) reads as Fixed', async () => {
    const pid = (await db.query(`insert into payroll_periods(starts_on, ends_on, paid_at, paid_by) values ('2025-06-01','2025-06-15',now(),$1) returning period_id`, [admin])).rows[0].period_id;
    await db.query(`insert into payroll_lines(period_id,user_id,kind,amount,created_by) values ($1,$2,'salary',8000,$3)`, [pid, desk, admin]);
    const r = await row('2025-06-01', desk);
    assert.equal(r.pay_type, 'fixed'); assert.equal(Number(r.base_pay), 8000); assert.equal(Number(r.net), 8000);
  });

  await test('H9: audit rows for pay type, reviews and corrections', async () => {
    const acts = (await db.query(`select action from audit_log where action like 'payroll.%'`)).rows.map(r => r.action);
    for (const a of ['payroll.salary_set', 'payroll.time_approved', 'payroll.time_rejected', 'payroll.time_reopened',
                     'payroll.hours_adjusted', 'payroll.hours_adjustment_removed'])
      assert.ok(acts.includes(a), a);
    const logs = await as(admin, `select entity, object_name from audit_records(null, null, '', 'payroll', '', 0, 200, null, '', '', '', '')`);
    const rev = logs.find(l => l.entity === 'payroll_time_reviews');
    assert.ok(rev && rev.object_name && !rev.object_name.includes('-'), 'readable name on review rows');
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nHourly payroll: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
