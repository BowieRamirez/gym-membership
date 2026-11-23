// 0096 (owner, 2026-11-18): the desk asks for a walk-in's refund, the System admin decides and repays. Day pass: the same day. Booking: 24 hours or more
// before the start, or later only when the gym cancels.
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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`wr-${++serial}@test.io`, JSON.stringify({ full_name: 'Refund Person', terms_version: '2026-11-15' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee');
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00' where id=1`);
  const court = (await db.query(`insert into amenities(name,price) values('WR Court',200) returning amenity_id id`)).rows[0].id;
  const dayAt = async n => (await db.query(`select ((now() at time zone 'Asia/Manila')::date + $1::int)::text d`, [n])).rows[0].d;
  const d3 = await dayAt(3), d5 = await dayAt(5);
  const at = (d, hm) => new Date(`${d}T${hm}:00+08:00`).toISOString();
  const walkIn = (name, start) => as(desk, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60)', [name, court, start]).then(r => r[0]);
  const dayPass = async name => {
    const p = (await db.query(`insert into payments(payment_code,kind,method,status,amount,walk_in_name,recorded_by,verified_by,verified_at,receipt_no)
      values(public.generate_payment_code(),'walk_in','cash','verified',150,$1,$2,$2,now(),public.next_receipt_no()) returning *`, [name, desk])).rows[0];
    await db.query('insert into gym_check_ins(payment_id,recorded_by) values($1,$2)', [p.payment_id, desk]);
    return p;
  };
  const ask = (id, reason = 'Changed plans', gym = false, by = desk) => as(by, 'select * from request_walk_in_refund($1,$2,$3)', [id, reason, gym]).then(r => r[0]);
  const decide = (id, ok, by = admin) => as(by, 'select decide_walk_in_refund($1,$2,$3)', [id, ok, 'Checked']);
  const status = async id => (await db.query('select status from reservations where reservation_id=$1', [id])).rows[0].status;
  const row = async id => (await db.query('select * from refund_requests where refund_request_id=$1', [id])).rows[0];

  await test('a Day pass: asked the same day, approved, repaid; the check-in stays', async () => {
    const p = await dayPass('Ana');
    const f = await ask(p.payment_id);
    assert.deepEqual([f.kind, f.status, Number(f.amount), f.member_id, f.reservation_id], ['day_pass', 'pending', 150, null, null]);
    assert.equal((await as(desk, 'select count(*)::int n from refund_requests where refund_request_id=$1', [f.refund_request_id]))[0].n, 1, 'the desk can follow it');
    await decide(f.refund_request_id, true);
    assert.equal((await row(f.refund_request_id)).status, 'approved');
    await as(admin, `select record_membership_refund_paid($1,'cash'::payment_method,null)`, [f.refund_request_id]);
    assert.equal((await row(f.refund_request_id)).status, 'paid');
    assert.equal((await db.query('select count(*)::int n from gym_check_ins where payment_id=$1', [p.payment_id])).rows[0].n, 1);
    await assert.rejects(ask(p.payment_id), /already_requested/);
  });
  await test('a Day pass from an earlier day is refused', async () => {
    const p = await dayPass('Old');
    await db.query(`update payments set verified_at=now()-interval '2 days' where payment_id=$1`, [p.payment_id]);
    await assert.rejects(ask(p.payment_id), /refund_day_passed/);
  });
  await test('a booking 24 hours or more ahead: approving cancels it and frees the slot', async () => {
    const start = at(d3, '09:00'), r = await walkIn('Far', start);
    const f = await ask(r.payment_id);
    assert.deepEqual([f.kind, f.reservation_id, await status(r.reservation_id)], ['walk_in_booking', r.reservation_id, 'confirmed']);
    await decide(f.refund_request_id, true);
    assert.equal(await status(r.reservation_id), 'cancelled');
    assert.equal((await walkIn('Next', start)).status, 'confirmed');
  });
  await test('rejecting changes nothing and cannot be decided twice', async () => {
    const r = await walkIn('Kept', at(d3, '12:00')), f = await ask(r.payment_id);
    await decide(f.refund_request_id, false);
    assert.equal(await status(r.reservation_id), 'confirmed');
    await assert.rejects(decide(f.refund_request_id, true), /already_processed/);
  });
  await test('under 24 hours: refused, unless the gym is the one cancelling', async () => {
    const r = await walkIn('Soon', at(d3, '15:00'));   // booked ahead, then moved close: booking it for "soon" fails in the evening, past closing time
    await db.query(`update reservations set starts_at = date_trunc('hour', now()) + interval '3 hours', ends_at = date_trunc('hour', now()) + interval '4 hours' where reservation_id = $1`, [r.reservation_id]);
    await assert.rejects(ask(r.payment_id), /refund_too_late/);
    const f = await ask(r.payment_id, 'Court broken', true);
    assert.match(f.reason, /^Gym is cancelling: Court broken/);
  });
  await test('only the desk asks; only the System admin decides', async () => {
    const m = await user(), p = await dayPass('Who');
    await assert.rejects(as(m, 'select request_walk_in_refund($1,$2)', [p.payment_id, 'x']), /forbidden/);
    const f = await ask(p.payment_id);
    await assert.rejects(as(desk, 'select decide_walk_in_refund($1,true,$2)', [f.refund_request_id, 'x']), /forbidden/);
    await assert.rejects(decide(f.refund_request_id, true, sub), /forbidden/);
    await assert.rejects(ask(p.payment_id, '   '), /bad_input|already_requested/);
  });
  await test('a member\'s payment cannot be refunded this way', async () => {
    const m = await user();
    const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('WR',1000,30) returning membership_package_id id`)).rows[0].id;
    const p = (await as(m, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    await assert.rejects(ask(p.payment_id), /not_refundable/);
  });
  await test('closing the day replaces an open request with the gym\'s own approved refund', async () => {
    const open = await walkIn('Open', at(d5, '09:00')), declined = await walkIn('Declined', at(d5, '11:00'));
    await ask(open.payment_id);
    const no = await ask(declined.payment_id); await decide(no.refund_request_id, false);
    await as(admin, 'select close_gym_day($1::date,$2)', [d5, 'Holiday']);
    for (const r of [open, declined]) {
      const rows = (await db.query('select kind, status from refund_requests where reservation_id=$1', [r.reservation_id])).rows;
      assert.deepEqual(rows, [{ kind: 'cancel', status: 'approved' }]);
    }
  });
  await test('anon cannot call either function', async () => {
    const r = (await db.query(`select has_function_privilege('anon','public.request_walk_in_refund(bigint,text,boolean)','execute') a,
      has_function_privilege('anon','public.decide_walk_in_refund(bigint,boolean,text)','execute') b`)).rows[0];
    assert.deepEqual([r.a, r.b], [false, false]);
  });
} catch (e) { failed++; console.log('  FAIL setup', e.message); }
finally {
  console.log(`\nwalk-in-refunds: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
