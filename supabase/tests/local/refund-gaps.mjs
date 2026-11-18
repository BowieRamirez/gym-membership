// 0095 (owner, 2026-11-18): three gaps in refunds. A walk-in has no member, which made cancel_reservation's checks NULL; a closed day's refund of
// down payment + balance could not be recorded as repaid; online money that could not be applied had no refund record to repay from.
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
async function service(sql, args = []) {
  await db.query('begin');
  try { await db.query('set local role service_role'); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`rg-${++serial}@test.io`, JSON.stringify({ full_name: 'Refund Person', terms_version: '2026-11-15' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin'), desk = await user('employee');
  await openCashShifts(db);
  await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',payment_expiry_hours=24 where id=1`);
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('RG Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const court = (await db.query(`insert into amenities(name,price) values('RG Court',200) returning amenity_id id`)).rows[0].id;
  const dayAt = async n => (await db.query(`select ((now() at time zone 'Asia/Manila')::date + $1::int)::text d`, [n])).rows[0].d;
  const today = await dayAt(0), d3 = await dayAt(3), d5 = await dayAt(5);
  const at = (d, hm) => new Date(`${d}T${hm}:00+08:00`).toISOString();
  const hoursAhead = async n => (await db.query(`select to_timestamp((ceil(extract(epoch from now()) / 3600) + $1::int) * 3600) t`, [n])).rows[0].t.toISOString();
  const buy = async uid => (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  const member = async () => { const uid = await user(); await as(desk, 'select verify_payment($1,true)', [(await buy(uid)).payment_id]); return uid; };
  const walkIn = (name, start, online = false) => as(desk, 'select * from desk_book_walk_in(p_name=>$1, p_amenity=>$2, p_start=>$3, p_minutes=>60, p_online=>$4)', [name, court, start, online]).then(r => r[0]);
  const book = (uid, start) => as(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method)`, [court, start]).then(r => r[0]);
  const cancel = (uid, id, reason) => as(uid, 'select cancel_reservation(p_id=>$1, p_reason=>$2)', [id, reason]);
  const repay = (id, by = admin) => as(by, `select record_membership_refund_paid($1,'cash'::payment_method,null)`, [id]);
  const ofBooking = async id => (await db.query('select * from refund_requests where reservation_id=$1', [id])).rows;
  const ofPayment = async id => (await db.query(`select * from refund_requests where payment_id=$1 and kind='online'`, [id])).rows;
  let xids = 0;
  const payOnline = async (p, amount) => {
    const xid = `rg-${++xids}`;
    await db.query('update payments set xendit_id=$1 where payment_id=$2', [xid, p.payment_id]);
    return { xid, result: (await service('select confirm_online_payment($1,$2,$3) r', [xid, amount, 'GCASH']))[0].r };
  };
  const expire = id => db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id=$1`, [id]);

  // ---------- 1: a walk-in's booking, which has no member ----------
  await test('the gym cancelling a walk-in under 24 hours before refunds it in full', async () => {
    const r = await walkIn('Soon', await hoursAhead(2));
    await cancel(desk, r.reservation_id, 'Court broken');
    const [f] = await ofBooking(r.reservation_id);
    assert.deepEqual([f?.status, Number(f?.amount), f?.member_id], ['approved', 200, null]);
  });
  await test('staff must give a reason to cancel a walk-in', async () => {
    const r = await walkIn('No Reason', at(d3, '06:00'));
    await assert.rejects(cancel(desk, r.reservation_id, null), /bad_reason/);
  });
  await test('a member cannot cancel a walk-in\'s booking', async () => {
    const r = await walkIn('Not Yours', at(d3, '08:00'));
    await assert.rejects(cancel(await user(), r.reservation_id, 'Mine now'), /not_found/);
    assert.equal((await db.query('select status from reservations where reservation_id=$1', [r.reservation_id])).rows[0].status, 'confirmed');
  });
  await test('a member cancelling their own booking under 24 hours still gets nothing back', async () => {
    const m = await member();
    const r = await book(m, await hoursAhead(4));
    await as(desk, 'select verify_payment($1,true)', [r.payment_id]);
    await cancel(m, r.reservation_id, null);
    assert.equal((await ofBooking(r.reservation_id)).length, 0);
  });

  // ---------- 2: a closed day refunds the down payment and the balance ----------
  const paidInFull = async hm => {
    const r = await book(await member(), at(d5, hm));
    await as(desk, 'select verify_payment($1,true)', [r.payment_id]);
    const bal = (await as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [r.reservation_id]))[0];
    return { r, bal };
  };
  const one = await paidInFull('09:00'), two = await paidInFull('11:00');
  await as(admin, 'select close_gym_day($1::date,$2)', [d5, 'Holiday']);
  await test('a closed day\'s refund of down payment + balance can be recorded as repaid', async () => {
    const [f] = await ofBooking(one.r.reservation_id);
    assert.equal(Number(f.amount), Number(one.r.down_payment) + Number(one.bal.amount));
    await repay(f.refund_request_id);
    assert.equal((await ofBooking(one.r.reservation_id))[0].status, 'paid');
  });
  await test('...but not once the balance has been voided: that money is no longer there to return', async () => {
    await db.query(`update payments set status='voided', voided_by=$1, voided_at=now(), void_reason='Test' where payment_id=$2`, [admin, two.bal.payment_id]);
    await assert.rejects(repay((await ofBooking(two.r.reservation_id))[0].refund_request_id), /not_refundable/);
  });

  // ---------- 3: online money that could not be applied ----------
  await test('a late online payment gets a refund to repay, with no staff member deciding it', async () => {
    const uid = await user(), p = await buy(uid);
    await expire(p.payment_id);
    assert.equal((await payOnline(p, p.amount)).result, 'refund');
    const [f] = await ofPayment(p.payment_id);
    assert.deepEqual([f?.status, Number(f?.amount), f?.member_id, f?.decided_by, f?.term_id, f?.reservation_id], ['approved', 1000, uid, null, null, null]);
    assert.ok(f.decided_at); assert.match(f.decision_reason, /no longer open/);
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='refund.request' and entity_id=$1`, [String(f.refund_request_id)])).rows[0].n, 1);
  });
  await test('Xendit retrying does not make a second refund', async () => {
    const p = await buy(await user());
    await expire(p.payment_id);
    const { xid } = await payOnline(p, p.amount);
    assert.equal((await service('select confirm_online_payment($1,$2,$3) r', [xid, p.amount, 'GCASH']))[0].r, 'refund');
    assert.equal((await ofPayment(p.payment_id)).length, 1);
  });
  await test('more money than the price is refunded in full and can be repaid', async () => {
    const p = await buy(await user());
    await payOnline(p, Number(p.amount) + 50);
    const [f] = await ofPayment(p.payment_id);
    assert.equal(Number(f.amount), 1050); assert.match(f.decision_reason, /1,050\.00/);
    await repay(f.refund_request_id);
    assert.equal((await ofPayment(p.payment_id))[0].status, 'paid');
  });
  await test('no usable amount from Xendit: the price is shown and staff are told to check Xendit', async () => {
    for (const amount of [null, -1]) {
      const p = await buy(await user());
      await payOnline(p, amount);
      const [f] = await ofPayment(p.payment_id);
      assert.equal(Number(f.amount), 1000); assert.match(f.decision_reason, /check .*Xendit/i);
    }
  });
  await test('a walk-in paying late is refunded too, with no member', async () => {
    const r = await walkIn('Late Walkin', at(d3, '10:00'), true);
    await expire(r.payment_id);
    await payOnline(r, 200);
    const [f] = await ofPayment(r.payment_id);
    assert.deepEqual([f?.member_id, Number(f?.amount)], [null, 200]);
    await repay(f.refund_request_id);
  });
  await test('a payment that already has a membership refund can still record stray online money', async () => {
    const uid = await user(), p = await buy(uid);
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    const t = (await db.query('select term_id from membership_terms where payment_id=$1', [p.payment_id])).rows[0];
    await as(uid, 'select * from request_membership_refund($1,$2)', [t.term_id, 'Moving']);
    assert.equal((await payOnline(p, p.amount)).result, 'refund');
    assert.equal((await db.query('select count(*)::int n from refund_requests where payment_id=$1', [p.payment_id])).rows[0].n, 2);
  });
  await test('it never touches revenue, but cash repaid does leave the drawer', async () => {
    const summary = async () => (await as(admin, 'select coalesce(sum(total),0)::numeric::text t from revenue_summary($1::date,$1::date)', [today]))[0].t;
    const days = async () => (await as(admin, 'select net::text, refunds::text from revenue_days($1::date,$1::date)', [today]))[0];
    const lines = async () => (await as(admin, 'select count(*)::int n from revenue_lines($1::date,$1::date,0,1000)', [today]))[0].n;
    const out = async () => (await db.query(`select cash_out::text c from cash_shift_totals((select shift_id from cash_shifts where cashier_id=$1 and closed_at is null))`, [admin])).rows[0].c;
    const before = [await summary(), await days(), await lines()], outBefore = Number(await out());
    const p = await buy(await user());
    await expire(p.payment_id);
    await payOnline(p, p.amount);
    await repay((await ofPayment(p.payment_id))[0].refund_request_id);
    assert.deepEqual([await summary(), await days(), await lines()], before);
    assert.equal(Number(await out()), outBefore + 1000);
  });
  await test('the new function is internal', async () => {
    const r = (await db.query(`select has_function_privilege('anon','public.record_online_refund(public.payments,numeric,text)','execute') a,
      has_function_privilege('authenticated','public.record_online_refund(public.payments,numeric,text)','execute') b`)).rows[0];
    assert.deepEqual([r.a, r.b], [false, false]);
  });
} catch (e) { failed++; console.log('  FAIL setup', e.message); }
finally {
  console.log(`\nrefund-gaps: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
