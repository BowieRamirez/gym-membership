// 0148 (owner, 2026-11-25, after the live test): an unpaid online booking holds its slot for 15 minutes, so a second person can't book
// and pay for the same hour while the first is paying. After the hold the slot is free again; a late payer still gets "Slot taken".
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
  [`sh-${++serial}@test.io`, JSON.stringify({ full_name: 'Hold Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00',payment_expiry_hours=24,down_payment_percent=50 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('SH Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = (await db.query(`insert into amenities(name,price) values('SH Ring',100) returning amenity_id id`)).rows[0].id;
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const book = (uid, hm) => as(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method)`, [amenity, at(hm)]).then(r => r[0]);
const expireHold = id => db.query(`update reservations set held_until = now() - interval '1 second' where reservation_id=$1`, [id]);

try {
  await test('an unpaid online booking holds the slot for about 15 minutes', async () => {
    const r = await book(await member(), '08:00');
    assert.equal(r.status, 'awaiting_payment');
    const mins = Number((await db.query(`select extract(epoch from held_until - now())/60 m from reservations where reservation_id=$1`, [r.reservation_id])).rows[0].m);
    assert.ok(mins > 14 && mins <= 15, `held for ${mins} min`);
  });
  await test('while held, another member, the desk and a desk walk-in cannot book the same slot', async () => {
    const a = await member(), b = await member();
    await book(a, '10:00');
    await assert.rejects(book(b, '10:00'), /slot_taken/);
    await assert.rejects(as(desk, `select * from desk_book_amenity($1,$2,$3,60)`, [b, amenity, at('10:00')]), /slot_taken/);
    await assert.rejects(as(desk, `select * from desk_book_walk_in('Walk In', $1, $2, 60, false)`, [amenity, at('10:00')]), /slot_taken/);
  });
  await test('the same member cannot hold the same slot twice', async () => {
    const a = await member();
    await book(a, '12:00');
    await assert.rejects(book(a, '12:00'), /slot_taken|member_busy/);
  });
  await test('others see a held slot as booked in the grid; the holder sees it as theirs', async () => {
    const a = await member(), b = await member();
    await book(a, '14:00');
    const at14 = x => String(x.amenity) === String(amenity) && new Date(x.starts).toISOString() === at('14:00');
    const mine = (await as(a, 'select amenity_grid($1::date) g', [day]))[0].g.blocks.filter(at14);
    const theirs = (await as(b, 'select amenity_grid($1::date) g', [day]))[0].g.blocks.filter(at14);
    assert.deepEqual([mine.length, mine[0].mine, theirs.length, theirs[0].mine], [1, true, 1, false]);
  });
  await test('after the hold runs out, someone else can book and pay for the slot', async () => {
    const a = await member(), b = await member();
    const first = await book(a, '16:00');
    await expireHold(first.reservation_id);
    const second = await book(b, '16:00');
    await as(desk, 'select verify_payment($1,true)', [second.payment_id]);
    assert.equal((await db.query('select status from reservations where reservation_id=$1', [second.reservation_id])).rows[0].status, 'confirmed');
  });
  await test('a desk online walk-in booking holds the slot against members too', async () => {
    const m = await member();
    await as(desk, `select * from desk_book_walk_in('Online Walk In', $1, $2, 60, true)`, [amenity, at('18:00')]);
    await assert.rejects(book(m, '18:00'), /slot_taken/);
  });
  await test('a paid booking is never held: cash at the desk confirms at once', async () => {
    const m = await member();
    const r = (await as(desk, `select * from desk_book_amenity($1,$2,$3,60)`, [m, amenity, at('20:00')]))[0];
    assert.equal(r.status, 'confirmed');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Slot hold: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
