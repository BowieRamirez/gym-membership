// Owner, 2026-11-17 (0090): an amenity's price is per hour, and so is its coach fee; bookings are whole hours, from 1 hour up to
// closing time (no 2-hour cap). The member's booking and the desk's booking both follow it.
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
  [`hp-${++serial}@test.io`, JSON.stringify({ full_name: 'Hourly Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  const desk = await user('employee'), coach = await user('coach');
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='06:00',closes_at='22:00',down_payment_percent=50 where id=1`);
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('H Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const member = async () => { const m = await user(null); const p = (await as(m, 'select * from extend_membership($1,null)', [pack]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return m; };
  const ring = (await db.query(`insert into amenities(name,price,coach_fee) values('H Ring',100,50) returning amenity_id id`)).rows[0].id;
  const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
  const at = (hm) => new Date(`${day}T${hm}:00+08:00`).toISOString();
  const book = (uid, hm, minutes, coachId = null) => as(uid, 'select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>$3, p_method=>$4::payment_method, p_coach=>$5)',
    [ring, at(hm), minutes, 'qr', coachId]).then(r => r[0]);
  await db.query('insert into coach_amenities(coach_id, amenity_id) values ($1,$2)', [coach, ring]);
  await db.query('insert into shift_posts(coach_id, starts_at, ends_at) values ($1,$2,$3)', [coach, at('06:00'), at('22:00')]);

  await test('2 hours cost twice the hourly price; the down payment is half of that', async () => {
    const r = await book(await member(), '08:00', 120);
    assert.deepEqual([Number(r.total_price), Number(r.down_payment)], [200, 100]);
  });

  await test('the coach fee is per hour too', async () => {
    const r = await book(await member(), '12:00', 120, coach);
    assert.deepEqual([Number(r.total_price), r.coach_id], [300, coach]);
  });

  await test('longer than 2 hours is fine within gym hours; past closing is refused', async () => {
    const r = await book(await member(), '16:00', 240);
    assert.equal(Number(r.total_price), 400);
    await assert.rejects(book(await member(), '20:00', 180), /outside_gym_hours/);
  });

  await test('half hours are refused: 30 and 90 minutes', async () => {
    const m = await member();
    await assert.rejects(book(m, '06:00', 30), /bad_input/);
    await assert.rejects(book(m, '06:00', 90), /bad_input/);
  });

  await test('the desk takes the hourly price for every hour', async () => {
    const r = (await as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>180)', [await user(null), ring, at('09:00')]))[0];
    assert.equal(Number(r.total_price), 300);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Hourly pricing: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
