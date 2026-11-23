// Phase 9 of the 2026-11-12 plan: try to break everything 0059 to 0066 added, as a member, a coach and the front desk.
// Each earlier file tests its own phase; this one goes across them by role, plus direct table writes and hostile input.
import { start, openCashShifts } from './harness.mjs';
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
    return r;
  } catch (e) { await db.query('rollback'); throw e; }
}
const rows = (uid, sql, args) => as(uid, sql, args).then(r => r.rows);
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`of-${++serial}@test.io`, JSON.stringify({ full_name: 'Break Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const refused = /forbidden|permission denied/;

const admin = await user('admin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('OF Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const court = (await db.query(`insert into amenities(name,price) values('OF Court',200) returning amenity_id id`)).rows[0].id;
async function member() {
  const uid = await user();
  const p = (await rows(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const later = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 9)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const book = (uid, hm, c = null) => rows(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method, p_coach=>$3)`,
  [court, at(hm), c]).then(r => r[0]);
await db.query('insert into coach_amenities(coach_id, amenity_id) values ($1,$2)', [coach, court]);
await db.query('insert into shift_posts(coach_id, starts_at, ends_at) values ($1,$2,$3)', [coach, at('08:00'), at('20:00')]);

try {
  const alex = await member(), bea = await member();
  // things for the attacks to aim at, made the proper way by the admin
  const code = (await rows(admin, `select create_discount('OFHIDE',15,now()+interval '30 days',10,false,$1::jsonb) id`,
    [JSON.stringify([{ kind: 'package', id: Number(pack) }])]))[0].id;
  const draftPack = (await rows(admin, `insert into membership_packages(name,price,duration_days) values('OF Draft',500,30) returning membership_package_id id`))[0].id;
  const draftAm = (await rows(admin, `insert into amenities(name,price) values('OF Draft Room',100) returning amenity_id id`))[0].id;
  const alexBooking = await book(alex, '09:00', coach);
  await as(desk, 'select verify_payment($1,true)', [alexBooking.payment_id]);

  console.log('\nAdmin-only functions, tried by a member, a coach and the front desk');
  const adminOnly = [
    [`select create_discount('OFMINE',90,now()+interval '1 day',1,true,'[{"kind":"package"}]'::jsonb)`, []],
    ['select set_discount_paused($1,true)', [code]],
    ['select delete_discount($1)', [code]],
    [`select publish_catalog_item('package',$1)`, [draftPack]],
    [`select publish_catalog_item('amenity',$1)`, [draftAm]],
    ['select new_package_version($1)', [pack]],
    [`select delete_catalog_item('package',$1)`, [draftPack]],
    ['select closure_preview($1::date)', [later]],
    [`select close_gym_day($1::date,'Mine now')`, [later]],
    ['select reopen_gym_day($1::date)', [later]],
  ];
  for (const [who, name] of [[alex, 'member'], [coach, 'coach'], [desk, 'front desk']])
    await test(`a ${name} is refused by every admin-only function`, async () => {
      for (const [sql, args] of adminOnly) await assert.rejects(rows(who, sql, args), refused, sql);
    });
  await test('none of those attempts changed anything', async () => {
    const r = (await db.query(`select (select paused from discounts where discount_id=$1) p, (select published_at from membership_packages where membership_package_id=$2) pp,
      (select published_at from amenities where amenity_id=$3) pa, (select count(*) from gym_closures)::int c,
      (select count(*) from membership_packages where replaces_id=$4)::int v, (select count(*) from discounts where code='OFMINE')::int d`,
      [code, draftPack, draftAm, pack])).rows[0];
    assert.deepEqual(r, { p: false, pp: null, pa: null, c: 0, v: 0, d: 0 });
  });

  console.log('\nFront desk functions, tried by a member and a coach');
  for (const [who, name] of [[alex, 'member'], [coach, 'coach']])
    await test(`a ${name} can't take a booking balance or check anyone in (any method)`, async () => {
      await assert.rejects(rows(who, `select * from record_booking_balance($1,'cash'::payment_method)`, [alexBooking.reservation_id]), refused);
      for (const extra of [`p_method=>'id', p_id_type=>'passport'`, `p_method=>'manual', p_reason=>'Lost phone'`, `p_method=>'password'`])
        await assert.rejects(rows(who, `select record_check_in(p_member_id=>$1, ${extra})`, [bea]), refused, extra);
    });

  console.log("\nSomeone else's booking");
  await test("a member can't cancel another member's booking; a coach can't cancel one they don't coach", async () => {
    await assert.rejects(rows(bea, `select cancel_reservation($1,'mine now')`, [alexBooking.reservation_id]), /not_found/);
    const other = await user('coach');
    await assert.rejects(rows(other, `select cancel_reservation($1,'mine now')`, [alexBooking.reservation_id]), /not_found/);
    assert.equal((await db.query('select status from reservations where reservation_id=$1', [alexBooking.reservation_id])).rows[0].status, 'confirmed');
  });
  await test("the grid shows another member's booking as taken, never whose it is or its id", async () => {
    const g = (await rows(bea, 'select amenity_grid($1::date) g', [day]))[0].g;
    const b = g.blocks.find(x => x.amenity === Number(court));
    assert.ok(b, 'the taken slot is missing');
    assert.equal(b.mine, false); assert.equal(b.id, null);
    assert.ok(!JSON.stringify(g).includes(alex), 'the grid carries the member id');
  });
  await test('E2: book_amenity ignores p_coach and never attaches a coach to an amenity reservation', async () => {
    const r = (await rows(bea, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method, p_coach=>$3)`,
      [court, at('12:00'), coach]))[0];
    assert.equal(r.coach_id, null);
  });
  await test('staff and coaches have no membership, so they cannot book for themselves', async () => {
    for (const who of [desk, coach]) await assert.rejects(book(who, '14:00'), /no_active_membership/);
  });

  console.log('\nTables written directly, past the functions');
  await test('nobody adds or removes a closed day, a code item or a password proof by hand, admin included', async () => {
    for (const who of [alex, desk, admin]) {
      await assert.rejects(rows(who, `insert into gym_closures(closed_on, reason, closed_by) values($1::date,'x',$2)`, [later, who]), /permission denied/);
      await assert.rejects(rows(who, `insert into discount_items(discount_id, kind) values($1,'amenity')`, [code]), /permission denied/);
      await assert.rejects(rows(who, 'select * from desk_password_proofs'), /permission denied/);
    }
  });
  await test("the front desk can't publish, price or re-type catalog rows by hand, or rewrite a check-in", async () => {
    // refused (42501) or no row touched (RLS); any other error means the test aimed wrong
    const none = async (sql, args) => assert.equal((await as(desk, sql, args).catch(e => ({ rowCount: e.code === '42501' ? 0 : -1 }))).rowCount, 0, sql);
    await none('update membership_packages set published_at=now() where membership_package_id=$1', [draftPack]);
    await none('update amenities set duration_days=30 where amenity_id=$1', [court]);
    const ci = (await rows(desk, `select record_check_in(p_member_id=>$1, p_method=>'id', p_id_type=>'passport') id`, [bea]))[0].id;
    await none(`update gym_check_ins set id_type='other' where check_in_id=$1`, [ci]);
    assert.equal((await db.query('select id_type from gym_check_ins where check_in_id=$1', [ci])).rows[0].id_type, 'passport');
  });
  await test("an admin can't edit a code's terms by hand (no table write at all)", async () =>
    assert.rejects(rows(admin, 'update discounts set percent=90 where discount_id=$1', [code]), /discount_locked|permission denied/));

  console.log('\nHostile input');
  await test('a hidden code is never listed to members, and a coach or the desk sees no more than a member', async () => {
    for (const who of [alex, coach, desk]) {
      const codes = (await rows(who, 'select code from my_discount_codes()')).map(r => r.code);
      assert.ok(!codes.includes('OFHIDE'), 'hidden code listed');
      assert.ok(!(await rows(who, 'select code from packages_codes()')).some(r => r.code === 'OFHIDE'));
    }
  });
  await test('create_discount refuses bad items, bad percents and odd kinds', async () => {
    const mk = (pct, items) => rows(admin, `select create_discount($1,$2,now()+interval '9 days',5,true,$3::jsonb)`, [`OFX${++serial}`, pct, items]);
    await assert.rejects(mk(0, '[{"kind":"package"}]'), /bad_discount/);
    await assert.rejects(mk(101, '[{"kind":"package"}]'), /bad_discount/);
    await assert.rejects(mk(10, `[{"kind":"package'; drop table payments; --"}]`), /bad_input/);
    await assert.rejects(mk(10, '[{"kind":"package","id":999999}]'), /item_not_found/);
    await assert.rejects(mk(10, '{"kind":"package"}'), /no_items/);
  });
  await test('catalog functions refuse a made-up kind and a missing row', async () => {
    await assert.rejects(rows(admin, `select publish_catalog_item('profiles',$1)`, [draftPack]), /bad_input/);
    await assert.rejects(rows(admin, `select delete_catalog_item('payments',1)`), /bad_input/);
    await assert.rejects(rows(admin, 'select new_package_version(999999)'), /not_found/);
  });
  await test('closing a day refuses a past day, a blank or 201-character reason', async () => {
    const past = (await db.query(`select ((now() at time zone 'Asia/Manila')::date - 1)::text d`)).rows[0].d;
    await assert.rejects(rows(admin, `select close_gym_day($1::date,'Yesterday')`, [past]), /past|bad_input|closed_day/);
    await assert.rejects(rows(admin, `select close_gym_day($1::date,'   ')`, [later]), /bad_reason/);
    await assert.rejects(rows(admin, 'select close_gym_day($1::date,$2)', [later, 'x'.repeat(201)]), /bad_reason/);
  });
  await test('add_my_last_name refuses blanks, a name already complete, and a page-long name', async () => {
    const one = await user();
    await db.query(`update profiles set last_name='' where id=$1`, [one]);
    await assert.rejects(rows(one, `select add_my_last_name('   ')`), /bad_name/);
    await assert.rejects(rows(one, 'select add_my_last_name($1)', ['x'.repeat(5000)]), /bad_name|check|too_long|22001|23514/);
    await rows(one, `select add_my_last_name('Cruz')`);
    await assert.rejects(rows(one, `select add_my_last_name('Again')`), /name_complete/);
    await assert.rejects(rows(alex, `select add_my_last_name('Hijack')`), /name_complete/);   // can only ever touch your own
  });
  await test('an archived admin is locked out of the new admin functions', async () => {
    const gone = await user('admin');
    await db.query('update profiles set is_active=false where id=$1', [gone]);
    await assert.rejects(rows(gone, `select close_gym_day($1::date,'Ghost')`, [later]), /forbidden|inactive|permission/);
    await assert.rejects(rows(gone, `select publish_catalog_item('amenity',$1)`, [draftAm]), /forbidden|inactive|permission/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exit(failed ? 1 : 0);
}
