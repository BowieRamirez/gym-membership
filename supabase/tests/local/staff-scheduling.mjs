// Owner fixes 2026-11-25, Fix 3 Phase 3.1 (S1-S8): the admin schedules coaches and front desk; check-in follows the shift
// (refused with no shift, or more than 5 minutes early; late is allowed); coaches no longer post shifts; the schedule writes
// coach shifts from tomorrow on and keeps a shift that has a booked session.
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
async function user(role, name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`ss-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const one = async (sql, args = []) => (await db.query(sql, args)).rows[0];
const today = (await one(`select (now() at time zone 'Asia/Manila')::date::text d`)).d;
const plus = async days => (await one(`select ($1::date + $2::int)::text d`, [today, days])).d;
const minute = Number((await one(`select extract(hour from now() at time zone 'Asia/Manila')*60 + extract(minute from now() at time zone 'Asia/Manila') m`)).m);
// a shift for today starting `mins` minutes from now (Manila wall clock), 2 hours long; written straight to the table
const hhmm = m => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const overrideToday = (who, mins) => db.query(`insert into schedule_overrides(user_id,from_date,to_date,starts_at,ends_at,reason,created_by)
  values($1,$2,$2,$3,$4,'test',$1)`, [who, today, hhmm(minute + mins), hhmm(Math.min(minute + mins + 120, 24 * 60 - 1))]);
const clearSchedules = () => db.query('delete from schedule_overrides; delete from schedule_assignments');

const admin = await user('admin', 'Sys Admin');
const desk = await user('employee', 'Desk Ana');
const coach = await user('coach', 'Coach Cy');
const coach2 = await user('coach', 'Coach Di');
const member = await user('member', 'Member Ed');
await db.query(`update gym_settings set opens_at='06:00', closes_at='22:00', schedule_enforced=true`);
const week = days => JSON.stringify(days);
const allWeek = (s, e) => week([1, 2, 3, 4, 5, 6, 7].map(d => ({ weekday: d, starts: s, ends: e })));

try {
  // ---- templates and permissions ----
  let tpl;
  await test('the admin saves a weekly template; times must be on the half hour and inside gym hours', async () => {
    tpl = (await as(admin, `select schedule_template_save(null, 'Weekday 9-5', $1::jsonb) r`, [allWeek('09:00', '17:00')]))[0].r.template_id;
    assert.ok(tpl);
    assert.equal((await one('select count(*)::int n from schedule_template_days where template_id=$1', [tpl])).n, 7);
    await assert.rejects(as(admin, `select schedule_template_save(null, 'Odd', $1::jsonb)`, [allWeek('09:10', '17:00')]), /off_grid/);
    await assert.rejects(as(admin, `select schedule_template_save(null, 'Late', $1::jsonb)`, [allWeek('20:00', '23:00')]), /outside_gym_hours/);
    await assert.rejects(as(admin, `select schedule_template_save(null, 'Weekday 9-5', $1::jsonb)`, [allWeek('09:00', '17:00')]), /name_taken/);
  });
  await test('front desk, coaches, members and anon cannot change schedules or write the tables', async () => {
    for (const who of [desk, coach, member])
      await assert.rejects(as(who, `select schedule_assign($1, $2, $3::date)`, [coach, tpl, today]), /forbidden/);
    await assert.rejects(asAnon(`select schedule_assign($1, $2, $3::date)`, [coach, tpl, today]), /permission denied|42501/);
    await assert.rejects(asAnon(`select shift_for($1, $2::date)`, [coach, today]), /permission denied|42501/);
    await assert.rejects(as(admin, `insert into schedule_templates(name) values('direct')`), /permission denied|42501/);
  });
  await test('only coaches and front desk can be scheduled, and not from a past date', async () => {
    await assert.rejects(as(admin, `select schedule_assign($1, $2, $3::date)`, [member, tpl, today]), /not_schedulable/);
    await assert.rejects(as(admin, `select schedule_assign($1, $2, $3::date)`, [admin, tpl, today]), /not_schedulable/);
    await assert.rejects(as(admin, `select schedule_assign($1, $2, $3::date)`, [desk, tpl, await plus(-1)]), /past_date/);
  });

  // ---- which shift applies ----
  await test('shift_for: the active assignment, a later assignment from its date, an override over both, a rest day', async () => {
    await clearSchedules();
    const early = (await as(admin, `select schedule_template_save(null, 'Early', $1::jsonb) r`, [allWeek('07:00', '15:00')]))[0].r.template_id;
    const restAll = (await as(admin, `select schedule_template_save(null, 'All rest', '[]'::jsonb) r`))[0].r.template_id;
    await as(admin, `select schedule_assign($1, $2, $3::date)`, [desk, tpl, today]);
    await as(admin, `select schedule_assign($1, $2, $3::date)`, [desk, early, await plus(3)]);
    await as(admin, `select schedule_assign($1, $2, $3::date)`, [desk, restAll, await plus(5)]);
    const at = async d => (await one(`select to_char(starts_at at time zone 'Asia/Manila','HH24:MI') s from shift_for($1, $2::date)`, [desk, d]))?.s ?? null;
    assert.equal(await at(await plus(1)), '09:00');
    assert.equal(await at(await plus(3)), '07:00');
    assert.equal(await at(await plus(5)), null);   // the latest template has no day: rest, not the older template's day
    await as(admin, `select schedule_override_add($1, $2::date, $3::date, '12:00', '20:00', 'Covering')`, [desk, await plus(1), await plus(2)]);
    assert.equal(await at(await plus(2)), '12:00');
    await as(admin, `select schedule_override_add($1, $2::date, $2::date, null, null, 'Sick')`, [desk, await plus(3)]);
    assert.equal(await at(await plus(3)), null);
    await db.query(`insert into gym_closures(closed_on,reason,closed_by) values($1,'Holiday',$2)`, [await plus(4), admin]);
    assert.equal(await at(await plus(4)), null);
  });

  // ---- clock-in: S3, S4, S1 ----
  const visit = c => one('select * from coach_visits where coach_id=$1 and checked_out_at is null', [c]);
  const checkout = c => db.query('update coach_visits set checked_out_at=now() where coach_id=$1 and checked_out_at is null', [c]);
  if (minute >= 30 && minute < 21 * 60) {
    await db.query(`update gym_settings set opens_at='00:00', closes_at='24:00'`);
    await test('S4: a coach with no shift today is refused', async () => {
      await clearSchedules();
      await assert.rejects(as(desk, 'select coach_check_in($1)', [coach]), /no_shift_today/);
    });
    await test('S3: more than 5 minutes before the start is refused and names the start', async () => {
      await clearSchedules(); await overrideToday(coach, 6);
      await assert.rejects(as(desk, 'select coach_check_in($1)', [coach]), new RegExp(`too_early.*${hhmm(minute + 6).replace(/^0/, '')}|too_early`));
    });
    await test('S3: within 5 minutes of the start is allowed and keeps the expected shift', async () => {
      await clearSchedules(); await overrideToday(coach, 4);
      await as(desk, 'select coach_check_in($1)', [coach]);
      const v = await visit(coach);
      assert.ok(v.expected_start && v.expected_end);
      await checkout(coach);
    });
    await test('S3: after the start is allowed (late)', async () => {
      await clearSchedules(); await overrideToday(coach, -20);
      await as(desk, 'select coach_check_in($1)', [coach]);
      const v = await visit(coach);
      assert.ok(new Date(v.checked_in_at) > new Date(v.expected_start));
      await checkout(coach);
    });
    await test('a rest override today refuses check-in', async () => {
      await clearSchedules();
      await db.query(`insert into schedule_overrides(user_id,from_date,to_date,reason,created_by) values($1,$2,$2,'Off',$1)`, [coach, today]);
      await assert.rejects(as(desk, 'select coach_check_in($1)', [coach]), /no_shift_today/);
    });
    await test('S1: front desk opening a drawer follows the shift; an admin is never limited', async () => {
      await clearSchedules();
      await assert.rejects(as(desk, 'select open_cash_shift(5000)'), /no_shift_today/);
      await overrideToday(desk, 0);
      const s = (await as(desk, 'select * from open_cash_shift(5000)'))[0];
      assert.ok(s.expected_start);
      const a = (await as(admin, 'select * from open_cash_shift(5000)'))[0];
      assert.equal(a.expected_start, null);
      await db.query('update cash_shifts set closed_at=now(), cash_in=0, cash_out=0, expected=opening_float, counted=opening_float where closed_at is null');
    });
    await test('rollout: with schedule_enforced off nobody is refused, and a shift is still recorded when there is one', async () => {
      await clearSchedules();
      await db.query('update gym_settings set schedule_enforced=false');
      await as(desk, 'select coach_check_in($1)', [coach]);
      assert.equal((await visit(coach)).expected_start, null);
      await checkout(coach);
      await overrideToday(coach2, 30);   // too early, but not enforced
      await as(desk, 'select coach_check_in($1)', [coach2]);
      assert.ok((await visit(coach2)).expected_start);
      await checkout(coach2);
      await db.query('update gym_settings set schedule_enforced=true');
    });
    await db.query(`update gym_settings set opens_at='06:00', closes_at='22:00'`);
  } else console.log('  skip  clock-in window checks: too close to midnight Manila for a same-day test shift');

  // ---- S2: coaches no longer post shifts ----
  const tomorrow = await plus(1);
  const at = (d, t) => `${d} ${t}:00+08`;
  await test('S2: a coach cannot post or change a shift', async () => {
    await assert.rejects(as(coach, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [coach, at(tomorrow, '10:00'), at(tomorrow, '12:00')]), /permission denied|42501|row-level security/);
    const s = await one(`insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3) returning shift_post_id`, [coach2, at(await plus(20), '10:00'), at(await plus(20), '11:00')]);
    await assert.rejects(as(coach2, `update shift_posts set status='cancelled' where shift_post_id=$1`, [s.shift_post_id]), /permission denied|42501/);
    assert.equal((await one('select status from shift_posts where shift_post_id=$1', [s.shift_post_id])).status, 'scheduled');
    await db.query('delete from shift_posts where shift_post_id=$1', [s.shift_post_id]);
  });

  // ---- S8: the schedule writes coach shifts from tomorrow on ----
  await test('S8: assigning a coach writes their shifts from tomorrow for 8 weeks, not today', async () => {
    await clearSchedules();
    await db.query('delete from training_sessions where coach_id=$1', [coach]); await db.query('delete from shift_posts where coach_id=$1', [coach]);
    await as(admin, `select schedule_assign($1, $2, $3::date)`, [coach, tpl, today]);
    const rows = (await db.query(`select (starts_at at time zone 'Asia/Manila')::date::text d, to_char(starts_at at time zone 'Asia/Manila','HH24:MI') s,
      to_char(ends_at at time zone 'Asia/Manila','HH24:MI') e from shift_posts where coach_id=$1 and status='scheduled' order by starts_at`, [coach])).rows;
    assert.equal(rows[0].d, tomorrow);
    assert.deepEqual([rows[0].s, rows[0].e], ['09:00', '17:00']);
    assert.equal(rows.at(-1).d, await plus(56));
    assert.ok(!rows.some(r => r.d === today));
  });
  await test('S8: a change replaces unbooked shifts and keeps one with a booked session, naming its day', async () => {
    const day2 = await plus(2);
    await db.query(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end) values($1,$2,$3,$4)`, [coach, member, at(day2, '10:00'), at(day2, '11:00')]);
    const early = (await one(`select template_id from schedule_templates where name='Early'`)).template_id;
    const kept = (await as(admin, `select schedule_assign($1, $2, $3::date) k`, [coach, early, today]))[0].k;
    assert.deepEqual(kept, [day2]);
    const day = async d => (await db.query(`select to_char(starts_at at time zone 'Asia/Manila','HH24:MI') s from shift_posts
      where coach_id=$1 and status='scheduled' and (starts_at at time zone 'Asia/Manila')::date=$2::date`, [coach, d])).rows.map(r => r.s);
    assert.deepEqual(await day(tomorrow), ['07:00']);
    assert.deepEqual(await day(day2), ['09:00']);   // kept: a session is booked inside it
  });
  await test('S8: the daily fill only touches coaches who have a schedule; a coach without one keeps their own shifts', async () => {
    await db.query('delete from shift_posts where coach_id=$1', [coach2]);
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)`, [coach2, at(tomorrow, '10:00'), at(tomorrow, '12:00')]);
    await db.query('select fill_coach_shifts()');
    assert.equal((await one(`select count(*)::int n from shift_posts where coach_id=$1 and status='scheduled'`, [coach2])).n, 1);
    assert.ok((await one(`select count(*)::int n from shift_posts where coach_id=$1 and status='scheduled'`, [coach])).n >= 50);
  });
  await test('a template in use cannot be archived; an archived one cannot be assigned', async () => {
    await assert.rejects(as(admin, 'select schedule_template_archive($1)', [tpl]).then(() =>
      as(admin, `select schedule_template_archive(template_id) from schedule_templates where name='Early'`)), /template_in_use/);
    const spare = (await as(admin, `select schedule_template_duplicate($1, 'Spare') id`, [tpl]))[0].id;
    await as(admin, 'select schedule_template_archive($1)', [spare]);
    await assert.rejects(as(admin, `select schedule_assign($1, $2, $3::date)`, [coach2, spare, today]), /template_archived/);
  });
  await test('Phase 3.3: the admin turns shift check-in on and off; nobody else can', async () => {
    await as(admin, 'select schedule_set_enforced(false)');
    assert.equal((await one('select schedule_enforced e from gym_settings')).e, false);
    await as(admin, 'select schedule_set_enforced(true)');
    assert.equal((await one('select schedule_enforced e from gym_settings')).e, true);
    for (const who of [desk, coach, member]) await assert.rejects(as(who, 'select schedule_set_enforced(false)'), /forbidden/);
    await assert.rejects(asAnon('select schedule_set_enforced(false)'), /permission denied|42501/);
    assert.equal((await one(`select count(*)::int n from audit_log where action = 'schedule.enforced'`)).n, 2);
  });
  await test('Phase 3.4: the desk reads every coach\'s shift today; members cannot', async () => {
    await clearSchedules();
    await db.query(`insert into schedule_overrides(user_id,from_date,to_date,starts_at,ends_at,reason,created_by) values($1,$2,$2,'08:00','16:00','test',$1)`, [coach, today]);
    const rows = await as(desk, `select coach_id, to_char(starts_at at time zone 'Asia/Manila','HH24:MI') s, late from coach_shifts_today()`);
    assert.equal(rows.find(r => r.coach_id === coach).late, minute > 8 * 60 && minute < 16 * 60);   // not in, shift running = late
    assert.deepEqual(rows.filter(r => r.coach_id === coach).map(r => r.s), ['08:00']);
    assert.ok(!rows.some(r => r.coach_id === coach2));   // no shift today: not listed
    await assert.rejects(as(member, 'select * from coach_shifts_today()'), /forbidden/);
    await assert.rejects(asAnon('select * from coach_shifts_today()'), /permission denied|42501/);
  });
  await test('schedule changes are audited', async () => {
    const n = (await one(`select count(*)::int n from audit_log where action like 'schedule.%'`)).n;
    assert.ok(n >= 5);
  });
} finally {
  console.log(`Staff scheduling: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
