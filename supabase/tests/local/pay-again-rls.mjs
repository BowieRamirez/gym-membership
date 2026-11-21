// B1 (reopened): Pay again on a replaced package resolves newest_package_version server-side even when RLS hides the old versions from members.
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
async function user(role = 'member', name = 'Test User') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`pa-${++serial}@test.io`, JSON.stringify({ full_name: name, terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin', 'Admin One');
  const member = await user('member', 'Member One');

  // Create v1 (published), replace with v2 (published), replace with v3 (published & active)
  const v1 = (await as(admin, `insert into membership_packages(name,description,price,duration_days) values('Monthly v1','Old',1500,30) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [v1]);

  const v2 = (await as(admin, `select new_package_version($1) id`, [v1]))[0].id;
  await db.query(`update membership_packages set price = 1200 where membership_package_id = $1`, [v2]);
  await as(admin, `select publish_catalog_item('plan', $1)`, [v2]);

  const v3 = (await as(admin, `select new_package_version($1) id`, [v2]))[0].id;
  await db.query(`update membership_packages set price = 1000 where membership_package_id = $1`, [v3]);
  await as(admin, `select publish_catalog_item('plan', $1)`, [v3]);

  // Archived package with no replacement
  const dead = (await as(admin, `insert into membership_packages(name,description,price,duration_days) values('Dead','Archived',900,30) returning membership_package_id id`))[0].id;
  await as(admin, `select publish_catalog_item('plan', $1)`, [dead]);
  await db.query(`update membership_packages set is_active = false where membership_package_id = $1`, [dead]);

  await test('Member cannot read replaced versions v1/v2 under RLS, but extend_membership(v1) resolves to v3', async () => {
    const visible = await as(member, `select membership_package_id from membership_packages where membership_package_id in ($1, $2, $3)`, [v1, v2, v3]);
    assert.deepEqual(visible.map(r => Number(r.membership_package_id)), [Number(v3)]);

    const pay = (await as(member, `select * from extend_membership($1, null)`, [v1]))[0];
    assert.equal(Number(pay.membership_package_snapshot_id), Number(v3));
    assert.equal(Number(pay.amount), 1000);
    assert.equal(pay.status, 'pending');
  });

  await test('Archived package with no replacement still fails with not_found', async () => {
    await as(member, `select cancel_pending_payment(payment_id) from payments where member_id = $1 and status = 'pending'`, [member]);
    await assert.rejects(as(member, `select * from extend_membership($1, null)`, [dead]), /not_found/);
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally {
  await db.end();
  await server.stop();
}
