// Phase 8 of the 2026-11-09 plan: try to break what the plan added, and the app-called functions no earlier file attacked
// (archive_records, chat_threads, chat_search, cancel_pending_payment were missed when rules.mjs could not see RpcAsync<List<...>> calls).
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
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`p8-${++serial}@test.io`, JSON.stringify({ full_name: 'Break Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P8 Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const pool = (await db.query(`insert into amenities(name,price,duration_days) values('P8 Pool',150,null) returning amenity_id id`)).rows[0].id;
const pending = async uid => (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
async function member() {
  const uid = await user();
  await as(desk, 'select verify_payment($1,true)', [(await pending(uid)).payment_id]);
  return uid;
}
const archive = uid => db.query('update profiles set is_active=false where id=$1', [uid]);
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
const book = (uid, hm) => as(uid, `select * from book_amenity($1,$2,60,'qr'::payment_method)`, [pool, at(hm)]).then(r => r[0]);

try {
  const alex = await member(), bea = await member();
  await as(alex, `select send_support_message($1,'my locker code is zebra42')`, [alex]);

  console.log('\nArchive (archive_records)');
  await test('only an Admin or System admin reads the archive', async () => {
    for (const who of [alex, coach, desk])
      await assert.rejects(as(who, `select * from archive_records()`), /forbidden|permission/);
    assert.ok(Array.isArray(await as(admin, `select * from archive_records()`)));
  });
  await test('a hostile filter or a huge page is refused, not run', async () => {
    await assert.rejects(as(admin, `select * from archive_records($1)`, [`account' or 1=1 --`]), /bad_archive_filter/);
    await assert.rejects(as(admin, `select * from archive_records('','',null,null,'',0,100000)`), /bad_archive_filter/);
    await assert.rejects(as(admin, `select * from archive_records('','',null,null,'',-5,10)`), /bad_archive_filter/);
  });

  console.log('\nChat list and search (chat_threads, chat_search)');
  await test('another member never sees my chat with the desk', async () => {
    const mine = await as(bea, `select * from chat_threads()`);
    assert.ok(!mine.some(t => t.member_id === alex || t.with_id === alex), 'Bea sees a thread with Alex');
    const hits = await as(bea, `select * from chat_search('zebra42')`);
    assert.equal(hits.length, 0);
    assert.equal((await as(alex, `select * from chat_search('zebra42')`)).length, 1);
  });
  await test('search wildcards are literal: % and _ match nothing they do not spell', async () => {
    assert.equal((await as(alex, `select * from chat_search('%%')`)).length, 0);
    assert.equal((await as(alex, `select * from chat_search('__')`)).length, 0);
  });
  await test('an archived account cannot list or search chats', async () => {
    const gone = await member();
    await as(gone, `select send_support_message($1,'hello there')`, [gone]);
    await archive(gone);
    await assert.rejects(as(gone, `select * from chat_threads()`), /inactive|forbidden/);
    await assert.rejects(as(gone, `select * from chat_search('hello')`), /inactive|forbidden/);
  });

  console.log('\nCancelling a waiting payment (cancel_pending_payment)');
  await test("a member cannot cancel someone else's payment", async () => {
    const carl = await user(), p = await pending(carl);
    await assert.rejects(as(bea, `select cancel_pending_payment($1)`, [p.payment_id]), /not_found/);
    assert.equal((await db.query('select status from payments where payment_id=$1', [p.payment_id])).rows[0].status, 'pending');
  });
  await test('an amenity payment cannot be cancelled this way, and a verified one never', async () => {
    const r = await book(alex, '09:00');
    await assert.rejects(as(alex, `select cancel_pending_payment($1)`, [r.payment_id]), /not_found/);
    const dan = await user(), p = await pending(dan);
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    await assert.rejects(as(dan, `select cancel_pending_payment($1)`, [p.payment_id]), /already_processed/);
  });

  console.log('\nLive records (0053): the live feed follows the row rules');
  await test("a member reads none of another member's payments, check-ins or terms", async () => {
    await db.query(`insert into gym_check_ins(member_id, recorded_by) values ($1,$2)`, [alex, desk]);
    for (const t of ['payments', 'gym_check_ins', 'membership_terms']) {
      assert.ok((await db.query(`select count(*)::int n from ${t} where member_id=$1`, [alex])).rows[0].n > 0, `no ${t} row to hide`);
      assert.equal((await as(bea, `select * from ${t} where member_id=$1`, [alex])).length, 0, t);
    }
  });

  console.log('\nBookings, coach amenities, announcements, PINs, shifts');
  await test("a member cannot cancel someone else's booking", async () => {
    const r = await book(alex, '11:00');
    await assert.rejects(as(bea, `select cancel_reservation($1,'not mine')`, [r.reservation_id]), /not_found|forbidden/);
  });
  await test('an archived member cannot book', async () => {
    const gone = await member();
    await archive(gone);
    await assert.rejects(book(gone, '13:00'), /inactive|forbidden|no_active_membership/);
  });
  await test('nobody writes the coach-amenity list directly, a coach included', async () => {
    await assert.rejects(as(coach, `insert into coach_amenities(coach_id, amenity_id) values ($1,$2)`, [coach, pool]), /permission denied/);
    await assert.rejects(as(admin, `delete from coach_amenities`), /permission denied/);
  });
  await test('an announcement to an unknown audience or with no text is refused', async () => {
    await assert.rejects(as(admin, `select post_announcement('Pool closed', 'everyone; drop table x')`), /bad_input|audience/);
    await assert.rejects(as(admin, `select post_announcement('   ')`), /bad_input/);
  });
  await test('staff and coaches have no desk PIN to set', async () => {
    for (const who of [coach, desk, admin])
      await assert.rejects(as(who, `select set_desk_pin('1234')`), /forbidden|not_a_member|not_found/);
  });
  await test('a shift cannot be closed with a negative or missing count', async () => {
    await assert.rejects(as(desk, `select close_cash_shift(-1)`), /bad_input|counted/);
    await assert.rejects(as(desk, `select close_cash_shift(null)`), /bad_input|counted/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exitCode = failed ? 1 : 0;
}
