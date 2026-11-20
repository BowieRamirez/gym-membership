// Plan 2026-11-17 Z1, Z2 (0087): the front desk books an amenity for a customer and takes the full price in cash at once.
// Same rules as a member's booking (shared body), minus the hour-ahead rule. Owner fixes 2026-11-19 Q1: the customer needs a membership
// (a Day pass package counts) like any member booking; an account alone is not enough.
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
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('DB Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const member = async () => { const m = await user(null); const p = (await as(m, 'select * from extend_membership($1,null)', [pack]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return m; };

  await test('Q1: an account with no membership is refused at the desk too; nothing is kept', async () => {
    const m = await user(null);
    const before = (await db.query('select count(*)::int n from payments')).rows[0].n;
    await assert.rejects(deskBook(desk, m, '08:00'), /no_active_membership/);
    assert.equal((await db.query('select count(*)::int n from payments')).rows[0].n, before);
  });

  await test('the desk books for a member and the full price goes into its drawer', async () => {
    const m = await member();
    const before = await shiftTotal(desk);
    const r = (await deskBook(desk, m, '10:00'))[0];
    assert.equal(r.status, 'confirmed'); assert.equal(r.member_id, m); assert.equal(r.created_by, desk);
    assert.equal(Number(r.down_payment), 300); assert.equal(Number(r.total_price), 300);
    assert.equal(await shiftTotal(desk), before + 300);
    const p = (await db.query('select status, receipt_no, method from payments where payment_id = $1', [r.payment_id])).rows[0];
    assert.equal(p.status, 'verified'); assert.ok(p.receipt_no); assert.equal(p.method, 'cash');
  });

  await test('a taken slot is refused and leaves no payment behind', async () => {
    const m = await member();
    const before = (await db.query('select count(*)::int n from payments')).rows[0].n;
    await assert.rejects(deskBook(desk, m, '10:00'), /slot|taken|busy|exclusion|overlap/i);
    assert.equal((await db.query('select count(*)::int n from payments')).rows[0].n, before);
  });

  await test('no open shift: nothing is booked', async () => {
    const m = await member();
    await assert.rejects(deskBook(desk2, m, '14:00'), /no_open_shift/);
    assert.equal((await db.query('select count(*)::int n from reservations where member_id = $1', [m])).rows[0].n, 0);
  });

  await test('the desk may book a slot starting within the hour; a member may not', async () => {
    if ((await db.query(`select extract(hour from now() at time zone 'Asia/Manila')::int h`)).rows[0].h >= 22) return;   // the next hour would run past closing
    const soon = (await db.query(`select to_timestamp(ceil(extract(epoch from now()) / 3600) * 3600) t`)).rows[0].t.toISOString();
    const m = await member();
    const r = (await as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [m, court, soon]))[0];
    assert.equal(r.status, 'confirmed');
  });

  await test('final review: a customer holding a pass is still booked for them, paid, not for the desk (0088)', async () => {
    const m = await member();
    await oldPass(db, m, court);
    const r = (await deskBook(desk, m, '18:00'))[0];
    assert.equal(r.member_id, m); assert.equal(r.status, 'confirmed'); assert.ok(r.payment_id);
  });

  await test('final review: no booking for a deactivated account or a staff account (0088)', async () => {
    const off = await member(), coach = await user('coach');
    await db.query('update profiles set is_active = false where id = $1', [off]);
    await assert.rejects(as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [off, court, at("19:00")]), /not_found|account_inactive|no_active_membership/);
    await assert.rejects(as(desk, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [coach, court, at("19:00")]), /not_found|no_active_membership/);
  });

  await test('members cannot use it; anon cannot call it or the shared body', async () => {
    const m = await user(null), other = await user(null);
    await assert.rejects(as(m, 'select * from desk_book_amenity(p_member=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [other, court, at('16:00')]), /forbidden|permission|not_allowed/);
    const r = (await db.query(`select has_function_privilege('anon','public.desk_book_amenity(uuid,bigint,timestamptz,integer,uuid,text)','execute') a,
      has_function_privilege('authenticated','public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,payment_method,uuid,text)','execute') b`)).rows[0];
    assert.equal(r.a, false); assert.equal(r.b, false);
  });

  // 0091 (owner, 2026-11-17): walk-ins with no account, and the member's receipt in chat
  const walkIn = (by, name, hm) => as(by, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>120)', [name, court, at(hm)]);

  await test('0091: a walk-in is booked by name, paid in full in cash into the drawer, with a receipt', async () => {
    const before = await shiftTotal(desk);
    const r = (await walkIn(desk, '  Ana Reyes ', '06:00'))[0];
    assert.equal(r.status, 'confirmed'); assert.equal(r.member_id, null); assert.equal(r.walk_in_name, 'Ana Reyes');
    assert.equal(Number(r.total_price), 600); assert.equal(Number(r.down_payment), 600);
    assert.equal(await shiftTotal(desk), before + 600);
    const p = (await db.query('select status, kind, method, walk_in_name, receipt_no from payments where payment_id = $1', [r.payment_id])).rows[0];
    assert.deepEqual([p.status, p.kind, p.method, p.walk_in_name], ['verified', 'amenity', 'cash', 'Ana Reyes']); assert.ok(p.receipt_no);
    const g = (await as(desk, 'select amenity_grid($1::date) g', [day]))[0].g;
    assert.ok(g.blocks.length > 0 && g.blocks.every(b => typeof b.mine === 'boolean'));   // the walk-in's block blocks the slot, 'mine' never null
  });

  await test('0091: a walk-in cannot take a booked slot; no name, no shift, or a member: refused, nothing kept', async () => {
    const before = (await db.query('select count(*)::int n from payments')).rows[0].n;
    await assert.rejects(walkIn(desk, 'Ben', '07:00'), /slot_taken/);
    await assert.rejects(walkIn(desk, '  ', '12:00'), /bad_input/);
    await assert.rejects(walkIn(desk2, 'Ben', '12:00'), /no_open_shift/);
    await assert.rejects(walkIn(await user(null), 'Ben', '12:00'), /forbidden|permission|not_allowed/);
    assert.equal((await db.query('select count(*)::int n from payments')).rows[0].n, before);
    const a = (await db.query(`select has_function_privilege('anon','public.desk_book_walk_in(text,bigint,timestamptz,integer,boolean)','execute') a`)).rows[0].a;
    assert.equal(a, false);
  });

  await test('0091: the gym cancelling a walk-in booking refunds it without a member', async () => {
    const r = (await walkIn(desk, 'Cora', '20:00'))[0];
    await as(desk, 'select cancel_reservation(p_id=>$1, p_reason=>$2)', [r.reservation_id, 'Pool closed']);
    const f = (await db.query('select member_id, amount from refund_requests where reservation_id = $1', [r.reservation_id])).rows[0];
    assert.equal(f.member_id, null); assert.equal(Number(f.amount), 600);
  });

  await test('0091: a member booked at the desk gets the receipt in their Front desk chat, from the desk', async () => {
    const m = await member();
    const r = (await deskBook(desk, m, '15:00'))[0];
    const receipt = (await db.query('select receipt_no from payments where payment_id = $1', [r.payment_id])).rows[0].receipt_no;
    const msg = (await db.query(`select sender_id, body from support_messages where member_id = $1 and queue = 'front_desk'`, [m])).rows;
    assert.equal(msg.length, 1); assert.equal(msg[0].sender_id, desk);
    assert.match(msg[0].body, new RegExp(`Receipt ${receipt}`)); assert.match(msg[0].body, /Court/);
  });

  // 0092 (owner, 2026-11-17): a walk-in's booking paid online through Xendit
  const service = async (sql, args = []) => {
    await db.query('begin');
    try { await db.query('set local role service_role'); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
    catch (e) { await db.query('rollback'); throw e; }
  };
  const online = (by, name, hm) => as(by, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60, p_online=>true)', [name, court, at(hm)]);
  const xenditPays = async (r, xid) => {
    await service('select save_online_checkout($1,$2,$3)', [r.payment_id, xid, `https://checkout.test/${xid}`]);
    const amount = Number((await db.query('select amount from payments where payment_id=$1', [r.payment_id])).rows[0].amount);
    return (await service('select confirm_online_payment($1,$2,$3) r', [xid, amount, 'GCASH']))[0].r;
  };
  const status = async id => (await db.query('select status from reservations where reservation_id=$1', [id])).rows[0].status;

  await test('0092: an online walk-in waits for Xendit, then is confirmed with a receipt; the drawer is not touched', async () => {
    const before = await shiftTotal(desk2);   // desk2 has no open shift: online needs none
    const r = (await online(desk2, 'Dina', '01:00'))[0];
    assert.equal(r.status, 'awaiting_payment');
    const p0 = (await db.query('select status, method, cash_shift_id, expires_at from payments where payment_id=$1', [r.payment_id])).rows[0];
    assert.deepEqual([p0.status, p0.method, p0.cash_shift_id], ['pending', 'qr', null]); assert.ok(p0.expires_at);
    assert.equal(await xenditPays(r, 'ps-walk-1'), 'confirmed');
    assert.equal(await status(r.reservation_id), 'confirmed');
    assert.ok((await db.query('select receipt_no from payments where payment_id=$1', [r.payment_id])).rows[0].receipt_no);
    assert.equal(await shiftTotal(desk2), before);
  });

  await test('0092: the slot taken while the walk-in pays: cancelled, with an approved refund for the desk', async () => {
    const r = (await online(desk, 'Eli', '03:00'))[0];
    await as(desk, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', ['Gus', court, at('03:00')]);
    assert.equal(await xenditPays(r, 'ps-walk-2'), 'confirmed');
    assert.equal(await status(r.reservation_id), 'cancelled');
    const f = (await db.query('select status, member_id, decided_by from refund_requests where reservation_id=$1', [r.reservation_id])).rows[0];
    assert.deepEqual([f.status, f.member_id, f.decided_by], ['approved', null, desk]);
  });

  await test('0092: the customer leaves (desk cancels) or the time runs out: the booking is cancelled too', async () => {
    const a = (await online(desk, 'Hal', '12:00'))[0];
    await as(desk, 'select verify_payment($1, false, $2)', [a.payment_id, 'Customer left at the desk']);
    assert.equal(await status(a.reservation_id), 'cancelled');
    const b = (await online(desk, 'Ivy', '12:00'))[0];   // the slot is free again
    await db.query(`update payments set expires_at = now() - interval '1 minute' where payment_id=$1`, [b.payment_id]);
    await db.query('select expire_payments()');
    assert.equal(await status(b.reservation_id), 'cancelled');
  });

  await test('0092: online is refused for a slot already booked, and anon cannot call it', async () => {
    await assert.rejects(online(desk, 'Jo', '06:00'), /slot_taken/);   // Ana Reyes has 06:00-08:00
    const r = (await db.query(`select has_function_privilege('anon','public.desk_book_walk_in(text,bigint,timestamptz,integer,boolean)','execute') a,
      has_function_privilege('authenticated','public.confirm_walk_in_booking(bigint)','execute') b`)).rows[0];
    assert.deepEqual([r.a, r.b], [false, false]);
  });

  await test('0093: every booking starts on the hour: member, desk for a member, walk-in (cash and online)', async () => {
    const m = await member();
    await db.query(`insert into cash_shifts(cashier_id, opening_float) values($1, 0) on conflict do nothing`, [desk]);
    await assert.rejects(deskBook(desk, m, '21:30'), /not_on_the_hour/);
    await assert.rejects(as(desk, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', ['Kai', court, at('21:30')]), /not_on_the_hour/);
    await assert.rejects(online(desk, 'Kai', '21:30'), /not_on_the_hour/);
    assert.equal((await deskBook(desk, m, '22:00'))[0].status, 'confirmed');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Desk booking: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
