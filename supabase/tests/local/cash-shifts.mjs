// Phase 3 (F1-F3, 2026-11-09): the cash-drawer shift. Cash is only taken (or paid back) inside the cashier's open shift; closing
// shows expected = float + cash in - cash out against the count, and a closed shift never changes.
import { start } from './harness.mjs';
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
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`cs-${++serial}@test.io`, JSON.stringify({full_name: name}), JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee', 'Desk Dana'), desk2 = await user('employee');
const monthly = (await db.query(`insert into membership_packages(name,price,duration_days)
  values('Shift Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
await db.query(`update gym_settings set walk_in_price=150 where id=1`);
const walkIn = who => as(who, `select * from record_counter_payment('walk_in','cash',null,null,'Juan Walk',null,null)`);
const open = (who, float, note = null) => as(who, 'select * from open_cash_shift($1,$2)', [float, note]);
const close = (who, counted, note = null) => as(who, 'select * from close_cash_shift($1,$2)', [counted, note]);
const mine = who => as(who, 'select * from my_cash_shift()');

try {
  await test('the opening float is a gym setting, 5000 by default', async () => {
    assert.equal(Number((await db.query('select opening_float from gym_settings where id=1')).rows[0].opening_float), 5000);
  });
  await test('no open shift: cash at the counter is refused, GCash still works', async () => {
    await assert.rejects(walkIn(desk), /no_open_shift/);
    const g = (await as(desk, `select * from record_counter_payment('walk_in','gcash',null,null,'G Walk','REF-CS-1',null)`))[0];
    assert.equal(g.cash_shift_id, null);
    assert.deepEqual(await mine(desk), []);
  });
  await test('members and coaches cannot open, close or read a shift', async () => {
    for (const m of [await user('member'), await user('coach')]) {
      await assert.rejects(as(m, 'select * from open_cash_shift(5000, null)'), /forbidden/);
      await assert.rejects(as(m, 'select * from close_cash_shift(5000, null)'), /forbidden/);
      await assert.rejects(as(m, 'select * from my_cash_shift()'), /forbidden/);
    }
  });
  await test('a float other than the gym setting needs a note; a bad float is refused', async () => {
    await assert.rejects(open(desk, 4500), /note_required/);
    await assert.rejects(open(desk, -1, 'x'), /bad_input/);
    await assert.rejects(open(desk, null, 'x'), /bad_input/);
  });
  let shift;
  await test('open a shift; a second open shift for the same cashier is refused; another cashier opens their own', async () => {
    shift = (await open(desk, 5000))[0];
    assert.equal(Number(shift.opening_float), 5000); assert.equal(shift.closed_at, null);
    await assert.rejects(open(desk, 5000), /shift_open/);
    await open(desk2, 4000, 'Short float from the safe');
  });
  await test('cash taken inside the shift is tied to it and counted', async () => {
    const p = (await walkIn(desk))[0];
    assert.equal(p.cash_shift_id, shift.shift_id);
    const s = (await mine(desk))[0];
    assert.equal(Number(s.cash_in), 150); assert.equal(Number(s.cash_out), 0); assert.equal(Number(s.expected), 5150);
    assert.equal(Number((await mine(desk2))[0].cash_in), 0);   // one drawer per cashier
  });
  await test('a cash refund paid out comes off the paying cashier\'s shift; refused with no shift', async () => {
    const member = await user('member', 'Refund Rita');
    const pay = (await as(member, 'select * from extend_membership($1,null)', [monthly]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);   // QR payment: no drawer involved
    assert.equal((await db.query('select cash_shift_id from payments where payment_id=$1', [pay.payment_id])).rows[0].cash_shift_id, null);
    const term = (await as(member, 'select * from membership_terms'))[0];
    const req = (await as(member, 'select * from request_membership_refund($1,$2,$3)', [term.term_id, 'Moving away', 'change_of_mind']))[0];
    await as(admin, 'select decide_membership_refund($1,true,$2)', [req.refund_request_id, 'Approved']);
    await assert.rejects(as(admin, `select record_membership_refund_paid($1,'cash')`, [req.refund_request_id]), /no_open_shift/);
    const adminShift = (await open(admin, 5000))[0];
    await as(admin, `select record_membership_refund_paid($1,'cash')`, [req.refund_request_id]);
    assert.equal((await db.query('select cash_shift_id from refund_requests where refund_request_id=$1', [req.refund_request_id])).rows[0].cash_shift_id, adminShift.shift_id);
    const s = (await mine(admin))[0];
    assert.equal(Number(s.cash_out), 1000); assert.equal(Number(s.expected), 4000);
    await close(admin, 4000);
  });
  await test('a voided cash payment drops out of the expected cash', async () => {
    const p = (await walkIn(desk))[0];
    assert.equal(Number((await mine(desk))[0].expected), 5300);
    await db.query(`update payments set status='voided',voided_by=$1,voided_at=now(),void_reason='Mistake' where payment_id=$2`, [admin, p.payment_id]);
    assert.equal(Number((await mine(desk))[0].expected), 5150);
  });
  await test('closing: a count that differs needs a note; the closed shift keeps expected, count and difference', async () => {
    await assert.rejects(close(desk, 5100), /note_required/);
    await assert.rejects(close(desk, -5, 'x'), /bad_input/);
    const c = (await close(desk, 5100, 'Gave change twice'))[0];
    assert.equal(Number(c.expected), 5150); assert.equal(Number(c.counted), 5100); assert.equal(Number(c.cash_in), 150); assert.ok(c.closed_at);
    assert.deepEqual(await mine(desk), []);
  });
  await test('a closed shift never changes: no second close, no direct edit, new cash refused until a new shift', async () => {
    await assert.rejects(close(desk, 5150, 'again'), /no_open_shift/);
    await assert.rejects(as(desk, `update cash_shifts set counted=5150 where shift_id=$1`, [shift.shift_id]), /permission denied/);
    await assert.rejects(as(admin, `update cash_shifts set counted=5150 where shift_id=$1`, [shift.shift_id]), /permission denied/);
    await assert.rejects(walkIn(desk), /no_open_shift/);
    const again = (await open(desk, 5000))[0];
    assert.notEqual(again.shift_id, shift.shift_id);
  });
  await test('Admin and System admin list every shift with the cashier and difference; front desk and members cannot', async () => {
    const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date d`)).rows[0].d;
    for (const who of [admin, sub]) {
      const rows = await as(who, 'select * from cash_shifts_list($1,$1)', [today]);
      const r = rows.find(x => x.shift_id === shift.shift_id);
      assert.equal(r.cashier, 'Desk Dana'); assert.equal(Number(r.difference), -50); assert.equal(r.close_note, 'Gave change twice');
      assert.ok(rows.some(x => x.closed_at === null && x.cashier === 'Desk Dana'));   // the open one too, live totals
    }
    await assert.rejects(as(desk, 'select * from cash_shifts_list($1,$1)', [today]), /forbidden/);
    await assert.rejects(as(desk, 'select * from cash_shifts'), /permission denied/);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\n${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
