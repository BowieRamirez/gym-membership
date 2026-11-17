// Plan 2026-11-17 Z1, Z2 (0087): the front desk books an amenity for a customer and takes the full price in cash at once.
// Same rules as a member's booking (shared body), minus an active membership and the hour-ahead rule.
import { start, openCashShifts, oldPass } from './harness.mjs';
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
  [`db-${++serial}@test.io`, JSON.stringify({ full_name: 'Desk Booking Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const shiftTotal = async (uid) => Number((await db.query(
  `select coalesce(sum(p.amount),0) s from payments p join cash_shifts s on s.shift_id = p.cash_shift_id where s.cashier_id = $1 and p.status = 'verified'`, [uid])).rows[0].s);

try {
  const desk = await user('employee'), desk2 = await user('employee');
  await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30' where id=1`);
  const court = (await db.query(`insert into amenities(name,price) values('Court',300) returning amenity_id id`)).rows[0].id;
  const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
  const at = (hm) => new Date(`${day}T${hm}:00+08:00`).toISOString();
  const deskBook = (by, member, hm) => as(by, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [member, court, at(hm)]);
  await db.query(`insert into cash_shifts(cashier_id, opening_float) values($1, 1000)`, [desk]);

  await test('the desk books for someone with no membership and the full price goes into its drawer', async () => {
    const m = await user(null);
    const before = await shiftTotal(desk);
    const r = (await deskBook(desk, m, '10:00'))[0];
    assert.equal(r.status, 'confirmed'); assert.equal(r.member_id, m); assert.equal(r.created_by, desk);
    assert.equal(Number(r.down_payment), 300); assert.equal(Number(r.total_price), 300);
    assert.equal(await shiftTotal(desk), before + 300);
    const p = (await db.query('select status, receipt_no, method from payments where payment_id = $1', [r.payment_id])).rows[0];
    assert.equal(p.status, 'verified'); assert.ok(p.receipt_no); assert.equal(p.method, 'cash');
  });

  await test('a taken slot is refused and leaves no payment behind', async () => {
    const m = await user(null);
    const before = (await db.query('select count(*)::int n from payments')).rows[0].n;
    await assert.rejects(deskBook(desk, m, '10:00'), /slot|taken|busy|exclusion|overlap/i);
    assert.equal((await db.query('select count(*)::int n from payments')).rows[0].n, before);
  });

  await test('no open shift: nothing is booked', async () => {
    const m = await user(null);
    await assert.rejects(deskBook(desk2, m, '14:00'), /no_open_shift/);
    assert.equal((await db.query('select count(*)::int n from reservations where member_id = $1', [m])).rows[0].n, 0);
  });

  await test('the desk may book a slot starting within the hour; a member may not', async () => {
    const soon = (await db.query(`select to_timestamp(ceil(extract(epoch from now()) / 1800) * 1800) t`)).rows[0].t.toISOString();
    const m = await user(null);
    const r = (await as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [m, court, soon]))[0];
    assert.equal(r.status, 'confirmed');
  });

  await test('final review: a customer holding a pass is still booked for them, paid, not for the desk (0088)', async () => {
    const m = await user(null);
    await oldPass(db, m, court);
    const r = (await deskBook(desk, m, '18:00'))[0];
    assert.equal(r.member_id, m); assert.equal(r.status, 'confirmed'); assert.ok(r.payment_id);
  });

  await test('final review: no booking for a deactivated account or a staff account (0088)', async () => {
    const off = await user(null), coach = await user('coach');
    await db.query('update profiles set is_active = false where id = $1', [off]);
    await assert.rejects(as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [off, court, at('19:00')]), /not_found|account_inactive/);
    await assert.rejects(as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [coach, court, at('19:00')]), /not_found/);
  });

  await test('members cannot use it; anon cannot call it or the shared body', async () => {
    const m = await user(null), other = await user(null);
    await assert.rejects(as(m, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [other, court, at('16:00')]), /forbidden|permission|not_allowed/);
    const r = (await db.query(`select has_function_privilege('anon','public.desk_book_amenity(uuid,bigint,timestamptz,integer,uuid,text)','execute') a,
      has_function_privilege('authenticated','public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,payment_method,uuid,text)','execute') b`)).rows[0];
    assert.equal(r.a, false); assert.equal(r.b, false);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Desk booking: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
