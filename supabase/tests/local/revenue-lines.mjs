// Phase 4 (D11): receipt-level revenue lines for the PDF. They must add up to revenue_summary for the same range:
// verified payments on their Manila verification day, minus approved/paid refunds on their Manila approval day, voids excluded.
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
    values($1,$2,$3) returning id`, [`p4-${++serial}@test.io`, JSON.stringify({full_name: name}), JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee');
const monthly = (await db.query(`insert into membership_packages(name,price,duration_days)
  values('Phase Four Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
async function paid(name) {
  const uid = await user('member', name), pay = (await as(uid, 'select * from extend_membership($1,null)', [monthly]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  return { uid, pay };
}
const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date d`)).rows[0].d;
const lines = (from, to, offset = 0, limit = 500, who = admin) => as(who, 'select * from revenue_lines($1,$2,$3,$4)', [from, to, offset, limit]);
const summary = (from, to) => as(admin, 'select * from revenue_summary($1,$2)', [from, to]);
const money = rows => rows.reduce((s, r) => s + Number(r.amount ?? r.total), 0);

const ana = await paid('Ana Reyes'), ben = await paid('Ben Cruz'), voided = await paid('Void Person'), refunded = await paid('Refund Person');
await as(admin, `select void_payment($1,'Wrong payment')`, [voided.pay.payment_id]);
const term = (await as(refunded.uid, 'select * from membership_terms'))[0];
const request = (await as(refunded.uid, 'select * from request_membership_refund($1,$2)', [term.term_id, 'Changed my plans']))[0];
await as(admin, 'select decide_membership_refund($1,true,$2)', [request.refund_request_id, 'Approved']);
await as(desk, `select record_counter_payment('walk_in','cash',null,null,'Juan Walk',null,null)`);

try {
  await test('only revenue readers can list lines', async () => {
    await assert.rejects(lines(today, today, 0, 500, desk), /forbidden/);
    await assert.rejects(lines(today, today, 0, 500, ana.uid), /forbidden/);
  });
  await test('lines reconcile with revenue_summary: receipts, refunds on approval day, voids excluded', async () => {
    const rows = await lines(today, today), sum = await summary(today, today);
    assert.equal(money(rows).toFixed(2), money(sum).toFixed(2));
    assert.equal(rows.filter(r => !r.refund).length, sum.reduce((s, r) => s + Number(r.payments), 0));
    assert.ok(!rows.some(r => r.payment === String(voided.pay.payment_id)));
    const refund = rows.find(r => r.refund);
    assert.equal(Number(refund.amount), -1000); assert.equal(refund.payer, 'Refund Person'); assert.ok(refund.receipt);
    assert.equal(rows.filter(r => r.payment === String(refunded.pay.payment_id)).length, 2);
  });
  await test('labels: member or walk-in name, package or walk-in item, receipt number, method', async () => {
    const rows = await lines(today, today);
    const a = rows.find(r => r.payment === String(ana.pay.payment_id));
    assert.equal(a.payer, 'Ana Reyes'); assert.equal(a.item, 'Phase Four Monthly'); assert.equal(a.kind, 'membership'); assert.equal(a.method, 'qr'); assert.ok(a.receipt);
    const w = rows.find(r => r.kind === 'walk_in');
    assert.equal(w.payer, 'Juan Walk'); assert.equal(w.item, 'Walk-in'); assert.equal(w.method, 'cash');
  });
  await test('pages join up to the full result with no gaps or repeats', async () => {
    const all = await lines(today, today), pages = [];
    for (let offset = 0; ; offset += 2) { const page = await lines(today, today, offset, 2); pages.push(...page); if (page.length < 2) break; }
    assert.deepEqual(pages.map(r => `${r.payment}/${r.refund}`), all.map(r => `${r.payment}/${r.refund}`));
    assert.equal(new Set(all.map(r => `${r.payment}/${r.refund}`)).size, all.length);
  });
  await test('bad ranges and page sizes are refused', async () => {
    await assert.rejects(as(admin, `select * from revenue_lines($1::date, $1::date - 1, 0, 10)`, [today]), /bad_input/);
    await assert.rejects(lines(today, today, -1, 10), /bad_input/);
    await assert.rejects(lines(today, today, 0, 0), /bad_input/);
    await assert.rejects(lines(today, today, 0, 1001), /bad_input/);
  });
  await test('Manila day boundaries match revenue_summary', async () => {
    await db.query(`update payments set verified_at='2026-10-01 15:59:59+00' where payment_id=$1`, [ana.pay.payment_id]);   // 23:59:59 Manila, 1 Oct
    await db.query(`update payments set verified_at='2026-10-01 16:00:00+00' where payment_id=$1`, [ben.pay.payment_id]);   // 00:00 Manila, 2 Oct
    const first = await lines('2026-10-01', '2026-10-01'), second = await lines('2026-10-02', '2026-10-02');
    assert.deepEqual(first.map(r => r.payment), [String(ana.pay.payment_id)]);
    assert.deepEqual(second.map(r => r.payment), [String(ben.pay.payment_id)]);
    assert.equal(money(first).toFixed(2), money(await summary('2026-10-01', '2026-10-01')).toFixed(2));
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nRevenue lines: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
