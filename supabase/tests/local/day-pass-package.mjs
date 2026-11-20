// Owner fixes 2026-11-19 Q1 = (b), Q9: the Day pass is a package (is_day_pass). Its term ends at midnight Manila on the day it is paid,
// it opens amenity bookings for that day only, someone with a membership (live or queued) can't buy one, and the desk's walk-in
// Day pass with no account costs the Day pass package's price.
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
const user = async (role) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`dpp-${++serial}@test.io`, JSON.stringify({ full_name: 'Day Pass Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  const desk = await user('employee');
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',walk_in_price=150 where id=1`);
  const monthly = (await db.query(`insert into membership_packages(name,price,duration_days) values('DP Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const dayPass = (await db.query(`insert into membership_packages(name,price,duration_days,is_day_pass) values('Day pass',120,1,true) returning membership_package_id id`)).rows[0].id;
  const court = (await db.query(`insert into amenities(name,price) values('DP Court',300) returning amenity_id id`)).rows[0].id;
  const buy = async (m, pack) => { const p = (await as(m, `select * from avail_membership($1,null)`, [pack]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return p; };
  const inDays = (n, hm) => db.query(`select (((now() at time zone 'Asia/Manila')::date + $1::int) + $2::time) at time zone 'Asia/Manila' t`, [n, hm]).then(r => r.rows[0].t.toISOString());

  await test('a Day pass term ends at the next midnight in Manila and makes them a member now', async () => {
    const m = await user(null); const p = await buy(m, dayPass);
    const t = (await db.query(`select starts_at, ends_at, ends_at = (((starts_at at time zone 'Asia/Manila')::date + 1)::timestamp at time zone 'Asia/Manila') midnight
      from membership_terms where payment_id = $1`, [p.payment_id])).rows[0];
    assert.equal(t.midnight, true);
    assert.equal((await as(m, 'select is_active_member() a'))[0].a, true);
  });

  await test('a Day pass holder cannot book a day after the pass', async () => {
    const m = await user(null); await buy(m, dayPass);
    await assert.rejects(as(m, `select * from book_amenity($1,$2,60)`, [court, await inDays(2, '10:00')]), /outside_membership/);
  });

  await test('a member with a live membership cannot buy a Day pass, nor one with only a queued term', async () => {
    const m = await user(null); await buy(m, monthly);
    await assert.rejects(as(m, `select * from extend_membership($1,null)`, [dayPass]), /already_availed/);
  });

  await test('two Day pass packages cannot be on sale at once', async () => {
    await assert.rejects(db.query(`insert into membership_packages(name,price,duration_days,is_day_pass) values('Day pass 2',99,1,true)`), /duplicate key|unique/);
  });

  await test('the walk-in Day pass with no account costs the Day pass package price', async () => {
    const p = (await as(desk, `select * from record_counter_payment('walk_in'::payment_kind,'cash'::payment_method,null,null,'Walk In',null,null)`))[0];
    assert.equal(Number(p.amount), 120);
    const o = (await as(desk, `select * from start_desk_online_payment('walk_in'::payment_kind,null,null,'Walk In',null)`))[0];
    assert.equal(Number(o.amount), 120);
  });

  await test('the desk sells the Day pass package to a member account in cash: a term until midnight', async () => {
    const m = await user(null);
    const p = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, dayPass]))[0];
    assert.equal(Number(p.amount), 120);
    assert.equal((await db.query(`select count(*)::int n from membership_terms where member_id = $1 and ends_at <= now() + interval '1 day'`, [m])).rows[0].n, 1);
  });
  // 0106: a Day pass booking must end by the pass's midnight; other memberships keep the start-only rule
  await test('0106: a Day pass booking running past midnight is refused; 9 to 10 pm is booked', async () => {
    const m = await user(null); const p = await buy(m, dayPass);
    await db.query(`update membership_terms set starts_at = now() - interval '1 day', ends_at = $2 where payment_id = $1`, [p.payment_id, await inDays(2, '00:00')]);
    await assert.rejects(as(m, `select * from book_amenity($1,$2,120)`, [court, await inDays(1, '23:00')]), /outside_membership/);
    const ok = (await as(m, `select * from book_amenity($1,$2,60)`, [court, await inDays(1, '21:00')]))[0];
    assert.ok(ok.reservation_id);
  });

  await test('0106: a Monthly member booking past their term end still works', async () => {
    const m = await user(null); const p = await buy(m, monthly);
    await db.query(`update membership_terms set starts_at = now() - interval '1 day', ends_at = $2 where payment_id = $1`, [p.payment_id, await inDays(1, '22:00')]);
    const ok = (await as(m, `select * from book_amenity($1,$2,120)`, [court, await inDays(1, '21:00')]))[0];
    assert.ok(ok.reservation_id);
  });

  // final review: the flag travels with a new version and can't change once a package is on sale
  await test('review fix: a new version of the Day pass is a Day pass too', async () => {
    const admin = await user('admin');
    const draft = (await as(admin, 'select new_package_version($1) id', [dayPass]))[0].id;
    assert.equal((await db.query('select is_day_pass from membership_packages where membership_package_id = $1', [draft])).rows[0].is_day_pass, true);
  });
  await test('review fix: a package on sale cannot be turned into (or out of) the Day pass', async () => {
    const admin = await user('admin');   // admins edit packages as an app user; the guard is for them
    await assert.rejects(as(admin, 'update membership_packages set is_day_pass = false where membership_package_id = $1', [dayPass]), /package_published/);
    await db.query('update membership_packages set is_active = false where membership_package_id = $1', [dayPass]);   // the one-Day-pass index aside
    await assert.rejects(as(admin, 'update membership_packages set is_day_pass = true where membership_package_id = $1', [monthly]), /package_published/);
  });
} finally {
  console.log(`\nday-pass-package: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
