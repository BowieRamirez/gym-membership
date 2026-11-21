// Phase 16 (E13, Q1): Coach access add-on for active membership terms (scaled by days left, Day pass, Xendit confirm, expiry, renewal).
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
async function user(role = 'member', name = 'Test User') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`ca-${++serial}@test.io`, JSON.stringify({ full_name: name, terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin', 'Admin One');
  const coach = await user('coach', 'Coach One');
  const mMonthly = await user('member', 'Monthly Member');
  const mDay = await user('member', 'Day Member');
  const mInc = await user('member', 'Included Member');
  const mNone = await user('member', 'No Addon Member');
  const mNoTerm = await user('member', 'No Term Member');

  const pkgMonthly = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach,coach_addon_price) values('Monthly','Plain monthly',1500,30,false,1000) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgMonthly]);

  const pkgDay = (await as(admin, `insert into membership_packages(name,description,price,duration_days,is_day_pass,includes_coach,coach_addon_price) values('Day pass','Plain day pass',150,1,true,false,250) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgDay]);

  const pkgInc = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach) values('VIP','Coach included',3000,30,true) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgInc]);

  const pkgNone = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach,coach_addon_price) values('Basic','No addon',1000,30,false,null) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [pkgNone]);

  // Helper to give a member an active term ending at a Manila-calendar-day offset
  async function giveTerm(memberId, pkgId, pkgName, durationDays, daysLeftFromToday) {
    const umpId = (await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
      values($1,$2,'active',now()-interval '1 day', (((now() at time zone 'Asia/Manila')::date + $3::int)::timestamp + time '23:00') at time zone 'Asia/Manila')
      returning user_membership_package_id id`, [memberId, pkgId, daysLeftFromToday])).rows[0].id;
    return (await db.query(`insert into membership_terms(member_id,user_membership_package_id,membership_package_id,package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
      values($1,$2,$3,$4,$5,1500,now()-interval '1 day', (((now() at time zone 'Asia/Manila')::date + $6::int)::timestamp + time '23:00') at time zone 'Asia/Manila','purchase')
      returning term_id`, [memberId, umpId, pkgId, pkgName, durationDays, daysLeftFromToday])).rows[0].term_id;
  }

  // Monthly with 15 days left -> ceil(1000 * 15 / 30) = 500
  const monthlyTermId = await giveTerm(mMonthly, pkgMonthly, 'Monthly', 30, 15);
  await giveTerm(mInc, pkgInc, 'VIP', 30, 20);
  await giveTerm(mNone, pkgNone, 'Basic', 30, 20);

  // Day pass ending at Manila midnight tonight
  const dayUmpId = (await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
    values($1,$2,'active',now()-interval '1 hour', (((now() at time zone 'Asia/Manila')::date + 1)::timestamp) at time zone 'Asia/Manila')
    returning user_membership_package_id id`, [mDay, pkgDay])).rows[0].id;
  await db.query(`insert into membership_terms(member_id,user_membership_package_id,membership_package_id,package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
    values($1,$2,$3,'Day pass',1,150,now()-interval '1 hour', (((now() at time zone 'Asia/Manila')::date + 1)::timestamp) at time zone 'Asia/Manila','purchase')`, [mDay, dayUmpId, pkgDay]);

  await test('Monthly: scaled price (15/30 days = 500), pending blocks second buy, Xendit confirm activates term & allows free booking', async () => {
    const offer = (await as(mMonthly, `select * from coach_access_offer()`))[0];
    assert.equal(offer.offered, true);
    assert.equal(Number(offer.price), 500);
    assert.equal(offer.days_left, 15);

    // Cannot book before buying add-on
    const futureDay = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`,
      [coach, `${futureDay} 09:00:00+08`, `${futureDay} 17:00:00+08`]);
    await assert.rejects(as(mMonthly, `select book_coach_session($1,$2,1,null)`, [coach, `${futureDay} 10:00:00+08`]), /coach_not_offered/);

    const pay = (await as(mMonthly, `select * from buy_coach_access(null)`))[0];
    assert.equal(pay.kind, 'coach_addon');
    assert.equal(Number(pay.amount), 500);
    assert.equal(pay.status, 'pending');

    const offerWhilePending = (await as(mMonthly, `select * from coach_access_offer()`))[0];
    assert.equal(offerWhilePending.offered, false);
    assert.equal(offerWhilePending.reason, 'pending_payment');
    await assert.rejects(as(mMonthly, `select * from buy_coach_access(null)`), /pending_payment/);

    // Simulate Xendit checkout + webhook confirmation
    await db.query(`select save_online_checkout($1, 'xen-ca-1', 'https://xendit.test/1')`, [pay.payment_id]);
    const res = (await db.query(`select confirm_online_payment('xen-ca-1', 500, 'GCASH') r`)).rows[0].r;
    assert.equal(res, 'confirmed');

    const term = (await db.query(`select coach_access_at, coach_access_payment_id from membership_terms where term_id = $1`, [monthlyTermId])).rows[0];
    assert.ok(term.coach_access_at);
    assert.equal(Number(term.coach_access_payment_id), Number(pay.payment_id));

    const offerAfter = (await as(mMonthly, `select * from coach_access_offer()`))[0];
    assert.equal(offerAfter.offered, false);
    assert.equal(offerAfter.reason, 'already_bought');
    await assert.rejects(as(mMonthly, `select * from buy_coach_access(null)`), /already_bought/);

    const booked = (await as(mMonthly, `select book_coach_session($1,$2,1,null) r`, [coach, `${futureDay} 10:00:00+08`]))[0].r;
    assert.equal(booked.is_free, true);
  });

  await test('Discount code of kind coach applies to scaled price; 100% off activates immediately; package code refused', async () => {
    const mDisc = await user('member', 'Discount Member');
    await giveTerm(mDisc, pkgMonthly, 'Monthly', 30, 14); // ceil(1000 * 14 / 30) = 467

    await as(admin, `select create_discount('COACH50', 50, now()+interval '10 days', 10, true, '[{"kind":"coach","id":null}]'::jsonb)`);
    await as(admin, `select create_discount('PKG10', 10, now()+interval '10 days', 10, true, $1::jsonb)`,
      [JSON.stringify([{ kind: 'package', id: Number(pkgMonthly) }])]);

    const chk = (await as(mDisc, `select * from check_discount_code('COACH50', 'coach', $1)`, [pkgMonthly]))[0];
    assert.equal(Number(chk.you_pay), 233.5);

    await assert.rejects(as(mDisc, `select * from buy_coach_access('PKG10')`), /code_not_for_item/);

    const pay = (await as(mDisc, `select * from buy_coach_access('COACH50')`))[0];
    assert.equal(Number(pay.amount), 233.5);

    // Expire it, then use 100% off code -> immediately verified & activated
    await db.query(`update payments set expires_at = now() - interval '1 minute' where payment_id = $1`, [pay.payment_id]);
    await db.query(`select expire_payments()`);

    const offerAfterExp = (await as(mDisc, `select * from coach_access_offer()`))[0];
    assert.equal(offerAfterExp.offered, true);

    await as(admin, `select create_discount('COACHFREE', 100, now()+interval '10 days', 10, true, '[{"kind":"coach","id":null}]'::jsonb)`);
    const freePay = (await as(mDisc, `select * from buy_coach_access('COACHFREE')`))[0];
    assert.equal(freePay.status, 'verified');
    assert.equal(Number(freePay.amount), 0);
    const term = (await db.query(`select coach_access_at from membership_terms where member_id = $1`, [mDisc])).rows[0];
    assert.ok(term.coach_access_at);
  });

  await test('Day pass: not offered when no free coach hours today; offered at full price when free hour exists', async () => {
    const noShiftOffer = (await as(mDay, `select * from coach_access_offer()`))[0];
    assert.equal(noShiftOffer.offered, false);
    assert.equal(noShiftOffer.reason, 'no_free_coaches');
    await assert.rejects(as(mDay, `select * from buy_coach_access(null)`), /coach_not_offered/);

    // Add a shift today with free hours (use 22:00..23:00? Wait: use today 08:00..20:00 if gym open, or future-proof: check gym_settings closes_at)
    await db.query(`update gym_settings set opens_at = '00:00', closes_at = '23:00' where id = 1`);
    // Shift covering the next full hour today (never in the past)
    const nextHr = (await db.query(`select (date_trunc('hour', now()) + interval '1 hour')::text s, (date_trunc('hour', now()) + interval '2 hours')::text e`)).rows[0];
    await db.query(`insert into shift_posts(coach_id,starts_at,ends_at,status) values($1,$2,$3,'scheduled')`,
      [coach, nextHr.s, nextHr.e]);

    const dayOffer = (await as(mDay, `select * from coach_access_offer()`))[0];
    assert.equal(dayOffer.offered, true);
    assert.equal(dayOffer.is_day_pass, true);
    assert.equal(Number(dayOffer.price), 250);
  });

  await test('Refused states: no active term, coach included, no addon price, renewal creates fresh term without coach_access_at', async () => {
    assert.equal((await as(mNoTerm, `select * from coach_access_offer()`))[0].reason, 'no_active_term');
    assert.equal((await as(mInc, `select * from coach_access_offer()`))[0].reason, 'already_included');
    assert.equal((await as(mNone, `select * from coach_access_offer()`))[0].reason, 'not_offered');
    await assert.rejects(as(mNoTerm, `select * from buy_coach_access(null)`), /coach_not_offered/);
    await assert.rejects(as(mInc, `select * from buy_coach_access(null)`), /coach_not_offered/);
    await assert.rejects(as(mNone, `select * from buy_coach_access(null)`), /coach_not_offered/);

    // Renewal on mMonthly after current term ends: new term has coach_access_at = null and is offered again
    await db.query(`update membership_terms set ends_at = now() - interval '1 minute' where term_id = $1`, [monthlyTermId]);
    await db.query(`update user_membership_packages set status = 'expired' where member_id = $1 and status = 'active'`, [mMonthly]);
    await giveTerm(mMonthly, pkgMonthly, 'Monthly', 30, 30);
    const renewOffer = (await as(mMonthly, `select * from coach_access_offer()`))[0];
    assert.equal(renewOffer.offered, true);
    assert.equal(Number(renewOffer.price), 1000);
  });

  await test('Security: anon cannot call offer or buy; authenticated cannot directly update membership_terms or call activate_payment_target', async () => {
    await assert.rejects(asAnon(`select * from coach_access_offer()`), /forbidden|permission denied/);
    await assert.rejects(asAnon(`select * from buy_coach_access(null)`), /forbidden|permission denied/);
    await assert.rejects(as(mNone, `update membership_terms set coach_access_at = now() where member_id = $1`, [mNone]), /permission denied|row-level security/);
    await assert.rejects(as(mNone, `select activate_payment_target(1)`), /permission denied/);
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally {
  await db.end();
  await server.stop();
}
