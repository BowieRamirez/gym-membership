// Phase 10 (E5, E6, E7, E12): free_coaches, book_coach_session, 15-min held add-on, E6 limits & no-show block, E12 daily max, refunds.
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
const user = async (role, name = 'Test User') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`bcs-${++serial}@test.io`, JSON.stringify({ full_name: name, terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

// Future Manila date (+5 days)
const futureDay = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 5)::text d`)).rows[0].d;
const slot = (h) => `${futureDay} ${String(h).padStart(2, '0')}:00:00+08`;

try {
  const admin = await user('admin', 'Admin One');
  const desk = await user('employee', 'Desk One');
  await openCashShifts(db);   // 0164: the drawer straight in the table; opening one follows the front desk's shift
  const coach1 = await user('coach', 'Coach Alpha');
  const coach2 = await user('coach', 'Coach Beta');
  const mInc = await user(null, 'Member Inc');
  const mPlain = await user(null, 'Member Plain');
  const mNone = await user(null, 'Member None');
  const mExtra = await user(null, 'Member Extra');

  // Packages: Coach included, Add-on, No-coach-offered
  const pkgInc = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach) values('VIP','With coach',4000,30,true) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgInc]);
  const pkgAdd = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach,coach_addon_price) values('Plus','Add-on',2000,30,false,300) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgAdd]);
  const pkgNo = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach,coach_addon_price) values('Floor','No coach',1000,30,false,null) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgNo]);

  // Active terms covering futureDay
  for (const [m, pid, pname] of [[mInc, pkgInc, 'VIP'], [mExtra, pkgInc, 'VIP'], [mPlain, pkgAdd, 'Plus'], [mNone, pkgNo, 'Floor']]) {
    const umpId = (await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
      values($1,$2,'active',now()-interval '1 day', now()+interval '20 days') returning user_membership_package_id id`, [m, pid])).rows[0].id;
    await db.query(`insert into membership_terms(member_id,user_membership_package_id,membership_package_id,package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
      values($1,$2,$3,$4,30,1000,now()-interval '1 day', now()+interval '20 days','purchase')`, [m, umpId, pid, pname]);
  }

  // Coach1 has shift 08:00-20:00 on futureDay; Coach2 has NO shift
  await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`, [coach1, slot(8), slot(20)]);

  await test('free_coaches lists only coaches with a shift on that day', async () => {
    const rows = await as(mInc, `select * from free_coaches($1::date)`, [futureDay]);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].coach_id, coach1);
    assert.ok(rows[0].free_hours.includes(12));
  });

  await test('coach_not_offered when package has Coach included off and no add-on price', async () => {
    await assert.rejects(as(mNone, `select book_coach_session($1,$2,1,null)`, [coach1, slot(9)]), /coach_not_offered/);
  });

  await test('E12: B 12-2 booked, C 1-3 refused (coach_taken), C 2-4 booked, D 4-6 booked, 7th hour refused (coach_day_full)', async () => {
    const b1 = (await as(mInc, `select book_coach_session($1,$2,2,null) r`, [coach1, slot(12)]))[0].r;
    assert.equal(b1.is_free, true);
    await assert.rejects(as(mExtra, `select book_coach_session($1,$2,2,null)`, [coach1, slot(13)]), /coach_taken/);
    const b2 = (await as(mExtra, `select book_coach_session($1,$2,2,null) r`, [coach1, slot(14)]))[0].r;
    assert.equal(b2.is_free, true);

    // mPlain buys coach access first, then desk books 4-6 (2 hrs, free with term add-on) -> total 6 hrs booked (12-2, 2-4, 4-6)
    await db.query(`update membership_terms set coach_access_at = now() where member_id = $1`, [mPlain]);
    const b3 = (await as(desk, `select desk_book_coach_session($1,$2,$3,2,'cash',null) r`, [mPlain, coach1, slot(16)]))[0].r;
    assert.equal(b3.is_free, true);
    await assert.rejects(as(mPlain, `select desk_book_coach_session($1,$2,$3,1,'cash',null)`, [mPlain, coach1, slot(16)]), /forbidden/);

    // Now coach1 has 6 hrs booked -> free_coaches omits coach1 and 7th hour is refused with coach_day_full
    const free = await as(mInc, `select * from free_coaches($1::date)`, [futureDay]);
    assert.equal(free.length, 0);
    await assert.rejects(as(mExtra, `select book_coach_session($1,$2,1,null)`, [coach1, slot(18)]), /coach_day_full/);
  });

  await test('coach_not_offered when plain package has no coach_access_at; coach_taken blocks overlapping slot', async () => {
    const day2 = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 6)::text d`)).rows[0].d;
    const s2 = (h) => `${day2} ${String(h).padStart(2, '0')}:00:00+08`;
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`, [coach2, s2(10), s2(16)]);

    await db.query(`update membership_terms set coach_access_at = null where member_id = $1`, [mPlain]);
    await assert.rejects(as(mPlain, `select book_coach_session($1,$2,1,null)`, [coach2, s2(10)]), /coach_not_offered/);

    await as(admin, `select create_discount('COACH20', 20, now()+interval '10 days', 10, true, '[{"kind":"coach","id":null}]'::jsonb)`);
    const chk = (await as(mPlain, `select * from check_discount_code('COACH20', 'coach', $1)`, [pkgAdd]))[0];
    assert.ok(Number(chk.you_pay) > 0);

    await db.query(`update training_sessions set status = 'completed' where member_id = $1 and status = 'scheduled'`, [mInc]);
    await db.query(`update training_sessions set status = 'completed' where member_id = $1 and status = 'scheduled'`, [mExtra]);
    const ok1 = (await as(mExtra, `select book_coach_session($1,$2,1,null) r`, [coach2, s2(10)]))[0].r;
    assert.equal(ok1.is_free, true);
    await assert.rejects(as(mInc, `select book_coach_session($1,$2,1,null)`, [coach2, s2(10)]), /coach_taken/);

    await db.query(`update training_sessions set status = 'cancelled' where training_session_id = $1`, [ok1.training_session_id]);
    const ok = (await as(mInc, `select book_coach_session($1,$2,1,null) r`, [coach2, s2(10)]))[0].r;
    assert.equal(ok.is_free, true);
  });

  await test('E6: 2nd session same day refused for coach-included; 2 no-shows block member 7 days, desk still books', async () => {
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`, [coach2, slot(8), slot(12)]);
    await assert.rejects(as(mInc, `select book_coach_session($1,$2,1,null)`, [coach2, slot(8)]), /too_many_coach_sessions/);

    // Insert 2 recent no-shows for mInc
    await db.query(`insert into training_sessions(coach_id,member_id,title,scheduled_start,scheduled_end,status,is_paid_addon)
      values($1,$2,'NS1',now()-interval '2 days',now()-interval '2 days'+interval '1 hour','no_show',false),
            ($1,$2,'NS2',now()-interval '1 day',now()-interval '1 day'+interval '1 hour','no_show',false)`, [coach2, mInc]);
    const day3 = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 7)::text d`)).rows[0].d;
    const s3 = (h) => `${day3} ${String(h).padStart(2, '0')}:00:00+08`;
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`, [coach2, s3(10), s3(15)]);

    await assert.rejects(as(mInc, `select book_coach_session($1,$2,1,null)`, [coach2, s3(10)]), /coach_booking_blocked/);
    // Desk still books
    const dBook = (await as(desk, `select desk_book_coach_session($1,$2,$3,1,'cash',null) r`, [mInc, coach2, s3(10)]))[0].r;
    assert.equal(dBook.is_free, true);
  });

  await test('Package switch from Coach included to plain keeps existing booked sessions free, new ones require add-on', async () => {
    const day5 = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 9)::text d`)).rows[0].d;
    const s5 = (h) => `${day5} ${String(h).padStart(2, '0')}:00:00+08`;
    await db.query(`update training_sessions set status = 'completed' where member_id = $1 and status = 'scheduled'`, [mExtra]);
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`, [coach2, s5(10), s5(15)]);
    const freeSes = (await as(mExtra, `select book_coach_session($1,$2,1,null) r`, [coach2, s5(10)]))[0].r;
    assert.equal(freeSes.is_free, true);

    // End mExtra's VIP term and start a Plus (add-on) term
    await db.query(`update membership_terms set ends_at = now() - interval '1 hour' where member_id = $1`, [mExtra]);
    await db.query(`update user_membership_packages set status = 'expired' where member_id = $1 and status = 'active'`, [mExtra]);
    const ump2 = (await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
      values($1,$2,'active',now()-interval '1 minute', now()+interval '20 days') returning user_membership_package_id id`, [mExtra, pkgAdd])).rows[0].id;
    await db.query(`insert into membership_terms(member_id,user_membership_package_id,membership_package_id,package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
      values($1,$2,$3,'Plus',30,2000,now()-interval '1 minute', now()+interval '20 days','purchase')`, [mExtra, ump2, pkgAdd]);

    const existing = (await db.query(`select status, is_paid_addon from training_sessions where training_session_id = $1`, [freeSes.training_session_id])).rows[0];
    assert.equal(existing.status, 'scheduled');
    assert.equal(existing.is_paid_addon, false);

    // Complete freeSes, then next session is refused until term coach_access_at is bought
    await db.query(`update training_sessions set status = 'completed' where training_session_id = $1`, [freeSes.training_session_id]);
    await assert.rejects(as(mExtra, `select book_coach_session($1,$2,1,null) r`, [coach2, s5(11)]), /coach_not_offered/);
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally { await db.end(); await server.stop(); }
