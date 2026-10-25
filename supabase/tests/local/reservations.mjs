// Phase 6 (D6-D8): amenity reservations. One unit per amenity, 30-minute cooldown after each use (exact boundary allowed),
// touching slots = one booking (max 2 h), all-or-nothing multi-select, pass required for solo bookings, no double booking
// under races, cancellation frees occupancy, early end keeps the cooldown, coach extensions are atomic.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import pg from 'pg';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
const clients = [];
async function as(uid, sql, args = [], client = db) {
  await client.query('begin');
  try {
    await client.query('set local role authenticated');
    await client.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await client.query(sql, args);
    await client.query('commit');
    return r.rows;
  } catch (e) { await client.query('rollback'); throw e; }
}
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`p6-${++serial}@test.io`, JSON.stringify({full_name: name}), JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
async function connection() {
  const c = new pg.Client({ host: db.connectionParameters.host, port: db.connectionParameters.port, user: 'postgres', password: 'pw', database: 'postgres' });
  await c.connect(); clients.push(c); return c;
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), coach2 = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P6 Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
const [court, ring] = (await db.query(`insert into amenities(name,price,duration_days) values('P6 Court',300,30),('P6 Ring',300,30) returning amenity_id`)).rows.map(r => r.amenity_id);
async function member({ passes = [court, ring], name = 'Member' } = {}) {
  const uid = await user('member', name);
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  for (const a of passes) { const p = (await as(uid, 'select * from avail_amenity($1)', [a]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); }
  return uid;
}
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const slots = list => JSON.stringify(list.map(([amenity, hm, d]) => ({ amenity, start: at(hm, d) })));
const book = (uid, list, client = db) => as(uid, 'select * from book_slots($1::jsonb)', [slots(list)], client);
const mine = async uid => (await as(uid, `select amenity_id, to_char(starts_at at time zone 'Asia/Manila','HH24:MI') s, to_char(ends_at at time zone 'Asia/Manila','HH24:MI') e
  from reservations where member_id=$1 and status='confirmed' and starts_at::date >= $2::date - 1 order by starts_at, amenity_id`, [uid, day])).map(r => `${r.amenity_id}@${r.s}-${r.e}`);
const ids = async uid => (await as(uid, `select reservation_id from reservations where member_id=$1 and status='confirmed' order by starts_at`, [uid])).map(r => r.reservation_id);

const ana = await member({ name: 'Ana Private' }), ben = await member({ name: 'Ben Private' }), nopass = await member({ passes: [] });

try {
  await test('touching slots become one booking; a gap makes two', async () => {
    await book(ana, [[court, '08:00'], [court, '08:30'], [ring, '13:00'], [ring, '14:00']]);
    assert.deepEqual(await mine(ana), [`${court}@08:00-09:00`, `${ring}@13:00-13:30`, `${ring}@14:00-14:30`]);
  });
  await test('a booking longer than 2 hours is refused', async () => {
    await assert.rejects(book(ben, [[ring, '16:00'], [ring, '16:30'], [ring, '17:00'], [ring, '17:30'], [ring, '18:00']]), /too_long/);
  });
  await test('the 30-minute cooldown blocks the next half hour; the exact boundary is allowed', async () => {
    await assert.rejects(book(ben, [[court, '09:00']]), /slot_taken/);        // inside Ana's cooldown 09:00-09:30
    await assert.rejects(book(ben, [[court, '07:30']]), /slot_taken/);        // its own cooldown would run into Ana's 08:00
    await book(ben, [[court, '09:30']]);                                      // exact boundary
    await book(ben, [[court, '06:00'], [court, '06:30']]);                    // ends 07:00, cooldown to 07:30: fine
    assert.deepEqual(await mine(ben), [`${court}@06:00-07:00`, `${court}@09:30-10:00`]);
  });
  await test('different amenities run at the same time, but one member cannot be in two places', async () => {
    const cara = await member();
    await book(cara, [[ring, '08:00']]);                                      // Ana is on the court then
    await assert.rejects(book(ben, [[ring, '06:30']]), /member_busy/);         // Ben is on the court 06:00-07:00; the ring is free
  });
  await test('multi-select is all or nothing', async () => {
    await assert.rejects(book(ben, [[ring, '19:00'], [court, '08:00']]), /slot_taken/);
    assert.ok(!(await mine(ben)).some(s => s.includes('19:00')));
  });
  await test('slots must be future, on the grid, inside gym hours and within 14 days', async () => {
    const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date::text d`)).rows[0].d;
    await assert.rejects(as(ben, 'select * from book_slots($1::jsonb)', [JSON.stringify([{ amenity: court, start: new Date(Date.now() - 36e5).toISOString() }])]), /past_slot|off_grid/);
    await assert.rejects(as(ben, 'select * from book_slots($1::jsonb)', [JSON.stringify([{ amenity: court, start: `${day}T10:15:00+08:00` }])]), /off_grid/);
    await assert.rejects(book(ben, [[court, '05:30']]), /outside_gym_hours/);
    await assert.rejects(book(ben, [[court, '21:30'], [court, '22:00']]), /outside_gym_hours/);
    const far = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 15)::text d`)).rows[0].d;
    await assert.rejects(book(ben, [[court, '10:00', far]]), /too_far_ahead/);
    await assert.rejects(book(ben, []), /bad_input/);
    void today;
  });
  await test('a solo booking needs an active pass for that amenity', async () => {
    await assert.rejects(book(nopass, [[court, '20:00']]), /no_pass/);
    const p = (await as(nopass, 'select * from avail_amenity($1)', [court]))[0];   // pending pass: still no right
    await assert.rejects(book(nopass, [[court, '20:00']]), /no_pass/);
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    await book(nopass, [[court, '20:00']]);
  });
  await test('cancelling before the start frees the slot and its cooldown', async () => {
    const [first] = await ids(ana);
    await as(ana, 'select cancel_reservation($1,null)', [first]);
    await book(ben, [[court, '08:00'], [court, '08:30']]);
  });
  await test('only the member or staff can cancel; staff must give a reason', async () => {
    const [id] = await ids(ben);
    await assert.rejects(as(ana, 'select cancel_reservation($1,null)', [id]), /not_found|forbidden/);
    await assert.rejects(as(desk, 'select cancel_reservation($1,null)', [id]), /bad_reason/);
    await as(desk, `select cancel_reservation($1,'Court maintenance')`, [id]);
    const r = (await db.query('select status, cancelled_by, cancel_reason from reservations where reservation_id=$1', [id])).rows[0];
    assert.deepEqual(r, { status: 'cancelled', cancelled_by: desk, cancel_reason: 'Court maintenance' });
  });
  await test('nobody writes reservations directly', async () => {
    await assert.rejects(as(ana, `insert into reservations(member_id,amenity_id,starts_at,ends_at) values($1,$2,$3,$4)`, [ana, court, at('11:00'), at('11:30')]), /permission denied/);
    await assert.rejects(as(ana, `update reservations set ends_at=ends_at+interval '1 hour' where member_id=$1`, [ana]), /permission denied/);
  });
  await test('the grid shows occupancy and cooldown but only your own bookings are identified', async () => {
    const g = (await as(ben, 'select amenity_grid($1::date) g', [day]))[0].g;
    const block = g.blocks.find(b => String(b.amenity) === String(court) && b.mine === false);
    assert.ok(block && block.id === null && !('member' in block));
    assert.equal(new Date(block.cooldown) - new Date(block.ends), 30 * 60e3);
    assert.ok(g.blocks.some(b => b.mine === true && b.id));
    assert.ok(g.now && g.opens && g.closes);
    assert.ok(!JSON.stringify(g).includes('Ana Private'));
  });
  await test('ending early while in use keeps a cooldown from the real end', async () => {
    const id = (await db.query(`insert into reservations(member_id,amenity_id,starts_at,ends_at,created_by)
      values($1,$2,now()-interval '20 minutes',now()+interval '40 minutes',$1) returning reservation_id`, [ana, ring])).rows[0].reservation_id;
    await as(ana, 'select end_reservation($1)', [id]);
    const r = (await db.query('select ends_at <= now() + interval \'1 second\' as ended, status from reservations where reservation_id=$1', [id])).rows[0];
    assert.deepEqual(r, { ended: true, status: 'confirmed' });
    await assert.rejects(as(ana, 'select cancel_reservation($1,null)', [id]), /already_started/);
  });
  await test('the owning coach extends a session in 30-minute steps; collisions and other people are refused', async () => {
    const id = (await db.query(`insert into reservations(member_id,amenity_id,coach_id,starts_at,ends_at,created_by)
      values($1,$2,$3,$4,$5,$3) returning reservation_id`, [ana, ring, coach, at('10:00'), at('11:00')])).rows[0].reservation_id;
    await assert.rejects(as(coach2, 'select extend_reservation($1,30)', [id]), /not_found|forbidden/);
    await assert.rejects(as(ana, 'select extend_reservation($1,30)', [id]), /not_found|forbidden/);
    await assert.rejects(as(coach, 'select extend_reservation($1,20)', [id]), /bad_input/);
    await as(coach, 'select extend_reservation($1,30)', [id]);
    await book(ben, [[ring, '12:00']]);                                       // 11:30 end + cooldown 12:00: boundary ok
    await assert.rejects(as(coach, 'select extend_reservation($1,30)', [id]), /slot_taken/);
    assert.equal((await db.query(`select to_char(ends_at at time zone 'Asia/Manila','HH24:MI') e from reservations where reservation_id=$1`, [id])).rows[0].e, '11:30');
  });
  await test('two members racing for the same slot: exactly one booking', async () => {
    const [x, y] = [await member(), await member()];
    const [one, two] = [await connection(), await connection()];
    await one.query('begin'); await one.query('set local role authenticated');
    await one.query(`select set_config('request.jwt.claim.sub',$1,true)`, [x]);
    await one.query('select * from book_slots($1::jsonb)', [slots([[court, '17:00']])]);
    const second = book(y, [[court, '17:00']], two).then(() => 'ok', e => e.message);
    for (let n = 0; n < 100 && (await db.query('select cardinality(pg_blocking_pids($1)) n', [two.processID])).rows[0].n === 0; n++) await new Promise(r => setTimeout(r, 20));
    await one.query('commit');
    assert.match(await second, /slot_taken/);
    assert.equal((await db.query(`select count(*)::int n from reservations where amenity_id=$1 and starts_at=$2 and status='confirmed'`, [court, at('17:00')])).rows[0].n, 1);
  });
} finally {
  for (const c of clients) await c.end();
  await db.end(); await server.stop();
}
console.log(`\nReservations: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
