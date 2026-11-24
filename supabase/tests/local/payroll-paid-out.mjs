// Paid-out periods list for Payroll page (0146).
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
async function user(role, name) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`ppo-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const n = v => Number(v);
const period = async s => (await as(admin, `select payroll_period($1) p`, [s]))[0].p;

const admin = await user('admin', 'Sys Admin');
const desk = await user('employee', 'Desk Ana');
const member = await user('member', 'Member Ben');
await as(admin, `select set_staff_salary($1, 13000, 'fixed')`, [desk]);

try {
  await test('non-admins and anon cannot read payroll_paid_out_periods', async () => {
    for (const who of [desk, member]) {
      await assert.rejects(as(who, `select * from payroll_paid_out_periods()`), /forbidden/);
    }
    await assert.rejects((async () => {
      await db.query('begin');
      try { await db.query('set local role anon'); return await db.query(`select * from payroll_paid_out_periods()`); }
      finally { await db.query('rollback'); }
    })(), /permission denied|forbidden/);
  });

  await test('only paid-out periods appear, newest first, with frozen totals matching payroll_period', async () => {
    const pDraft = await period('2026-07-01');
    const pApproved = await period('2026-08-01');
    await as(admin, `select payroll_mark_paid($1)`, [pApproved.period_id]);

    const p1 = await period('2026-09-01');
    await as(admin, `select * from payroll_add_line($1, $2, 'bonus', 1000, 'Cover', 'regular_bonus')`, [p1.period_id, desk]);
    await as(admin, `select payroll_mark_paid($1)`, [p1.period_id]);
    await as(admin, `select payroll_mark_paid_out($1)`, [p1.period_id]);
    const p1Detail = await period('2026-09-01');

    const p2 = await period('2026-09-16');
    await as(admin, `select payroll_mark_paid($1)`, [p2.period_id]);
    await as(admin, `select payroll_mark_paid_out($1)`, [p2.period_id]);
    const p2Detail = await period('2026-09-16');

    const list = await as(admin, `select *, starts_on::text starts_iso from payroll_paid_out_periods()`);
    assert.equal(list.length, 2);
    assert.equal(list[0].starts_iso, '2026-09-16');
    assert.equal(list[1].starts_iso, '2026-09-01');

    assert.equal(list[0].people, 1);
    assert.equal(n(list[0].gross), n(p2Detail.total_gross));
    assert.equal(n(list[0].gov_total), n(p2Detail.total_gov));
    assert.equal(n(list[0].net), n(p2Detail.total_net));
    assert.equal(list[0].paid_out_by_name, 'Sys Admin');
    assert.ok(list[0].paid_out_at);

    assert.equal(n(list[1].bonuses), 1000);
    assert.equal(n(list[1].gross), n(p1Detail.total_gross));
    assert.equal(n(list[1].net), n(p1Detail.total_net));
    assert.ok(!list.some(x => Number(x.period_id) === pDraft.period_id));
    assert.ok(!list.some(x => Number(x.period_id) === pApproved.period_id));
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nPayroll paid out: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
