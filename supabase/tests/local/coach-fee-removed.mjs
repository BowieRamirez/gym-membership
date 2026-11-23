// Phase 8 (E1, E2): coach hourly_rate and amenity coach_fee removed; amenity bookings cost amenity price only and book no coach.
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
  [`cfr-${++serial}@test.io`, JSON.stringify({ full_name: 'Fee Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const desk = await user('employee');
  const coach = await user('coach');
  const member = await user(null);
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00' where id=1`);
  const monthly = (await db.query(`insert into membership_packages(name,price,duration_days) values('CFR Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [member, monthly]);
  const court = (await db.query(`insert into amenities(name,price) values('CFR Court',200) returning amenity_id id`)).rows[0].id;
  const start1 = (await db.query(`select (((now() at time zone 'Asia/Manila')::date + 2) + '10:00'::time) at time zone 'Asia/Manila' t`)).rows[0].t.toISOString();
  const start2 = (await db.query(`select (((now() at time zone 'Asia/Manila')::date + 2) + '12:00'::time) at time zone 'Asia/Manila' t`)).rows[0].t.toISOString();

  await test('hourly_rate and coach_fee columns are gone', async () => {
    const cols = (await db.query(`select table_name, column_name from information_schema.columns where table_schema='public' and column_name in ('hourly_rate','coach_fee')`)).rows;
    assert.deepEqual(cols, []);
  });

  await test('amenity booking costs amenity price only and old-style call with coach books no coach', async () => {
    const r1 = (await as(member, `select * from book_amenity($1,$2,60,'qr',null,null)`, [court, start1]))[0];
    assert.equal(Number(r1.total_price), 200);
    assert.equal(r1.coach_id, null);

    // Old-style call passing p_coach is charged the same and books no coach
    const r2 = (await as(desk, `select * from desk_book_amenity($1,$2,$3,120,$4,null)`, [member, court, start2, coach]))[0];
    assert.equal(Number(r2.total_price), 400);
    assert.equal(r2.coach_id, null);
  });

  await test('past booking with coach fee still refunds its full paid amount', async () => {
    // Seed an older confirmed booking that had total_price=300 (e.g. 200 amenity + 100 legacy coach_fee)
    const start3 = (await db.query(`select (((now() at time zone 'Asia/Manila')::date + 3) + '10:00'::time) at time zone 'Asia/Manila' t`)).rows[0].t.toISOString();
    const ua = (await db.query(`insert into user_amenities(member_id,amenity_id) values($1,$2) returning user_amenity_id id`, [member, court])).rows[0].id;
    const pay = (await db.query(`insert into payments(payment_code,kind,method,status,amount,member_id,user_amenity_id,verified_by,verified_at,expires_at,receipt_no) values('PAY-OLD-CF','amenity','qr','verified',300,$1,$2,$3,now(),now() + interval '1 hour','OR-OLD-001') returning payment_id id`, [member, ua, desk])).rows[0].id;
    const res = (await db.query(`insert into reservations(member_id,amenity_id,starts_at,ends_at,created_by,user_amenity_id,payment_id,status,total_price,down_payment) values($1,$2,$3,$3::timestamptz + interval '1 hour',$1,$4,$5,'confirmed',300,300) returning reservation_id id`, [member, court, start3, ua, pay])).rows[0].id;
    await as(member, `select cancel_reservation($1,null)`, [res]);
    const req = (await db.query(`select amount from refund_requests where payment_id=$1`, [pay])).rows[0];
    assert.equal(Number(req.amount), 300);
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally { await db.end(); await server.stop(); }
