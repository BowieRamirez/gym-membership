import assert from 'node:assert/strict';
import { start } from './harness.mjs';

let legacyPlan;
const { server, client: db } = await start({ beforeMigration: async (client, file) => {
  if (file === '0024_archive_catalog.sql') legacyPlan = (await client.query(`insert into membership_packages(name,price,duration_days,is_active) values('Legacy inactive',10,30,false) returning membership_package_id`)).rows[0].membership_package_id;
} });
let passed = 0, failed = 0, sequence = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (error) { failed++; console.log('  FAIL', name, error.message); }
}
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,'{"full_name":"Archive Test"}',$2) returning id`, [`catalog-${++sequence}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function as(uid, sql, values = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub',$1,true)`, [uid]);
    const result = await db.query(sql, values); await db.query('commit'); return result;
  } catch (error) { await db.query('rollback'); throw error; }
}
const owner = await user('admin');
const member = await user();
const coach = await user('coach');
const desk = await user('employee');
const plan = (await db.query(`insert into membership_packages(name,price,duration_days) values('Retained plan',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
const amenity = (await db.query(`insert into amenities(name,price) values('Retained amenity',100) returning amenity_id`)).rows[0].amenity_id;
const code = (await db.query(`insert into discounts(code,percent,expires_at,max_uses) values('RETAIN10',10,now()+interval '30 days',100) returning discount_id`)).rows[0].discount_id;
const expired = (await db.query(`insert into discounts(code,percent,starts_at,expires_at,max_uses) values('OLD10',10,now()-interval '2 days',now()-interval '1 day',100) returning discount_id`)).rows[0].discount_id;
const list = (actor = owner, ...params) => as(actor, 'select * from archive_records($1,$2,$3,$4,$5,$6,$7)', [params[0] ?? '', params[1] ?? '', params[2] ?? null, params[3] ?? null, params[4] ?? '', params[5] ?? 0, params[6] ?? 31]);
const archive = (type, id, reason = 'Deactivated by admin') => as(owner, 'select archive_catalog_record($1,$2,$3)', [type, id, reason]);
const restore = (type, id, actor = owner) => as(actor, 'select restore_archived_record($1,$2)', [type, String(id)]);
try {
  await test('legacy inactive rows have honest unknown dates and reasons', async () => {
    const row = (await list(owner, 'plan')).rows.find(r => r.record_id === String(legacyPlan));
    assert.equal(row.archived_at, null); assert.equal(row.archived_by, null); assert.equal(row.reason, 'Unknown (legacy record)');
  });
  for (const actor of [member, coach, desk]) {
    await test('nonadmin cannot list or restore archive', async () => {
      await assert.rejects(list(actor), /forbidden/); await assert.rejects(restore('plan', legacyPlan, actor), /forbidden/);
      await assert.rejects(as(actor, `select archive_catalog_record('plan',$1,'reason')`, [plan]), /forbidden/);
    });
  }
  await test('archiving a used plan retains entitlements and history', async () => {
    await db.query('insert into user_membership_packages(member_id,membership_package_id) values($1,$2)', [member, plan]);
    await archive('plan', plan, 'Retired gym plan');
    assert.equal((await db.query('select is_active from membership_packages where membership_package_id=$1', [plan])).rows[0].is_active, false);
    assert.equal((await db.query('select count(*)::int n from user_membership_packages where membership_package_id=$1', [plan])).rows[0].n, 1);
  });
  await test('archive metadata is server stamped and audited', async () => {
    const row = (await list(owner, 'plan')).rows.find(r => r.record_id === String(plan));
    assert.ok(row.archived_at); assert.equal(row.archived_by, owner); assert.equal(row.reason, 'Retired gym plan');
    assert.equal((await db.query(`select details->>'reason' reason from audit_log where action='catalog.archive' and entity_id=$1`, [String(plan)])).rows[0].reason, 'Retired gym plan');
    await assert.rejects(as(owner, 'update membership_packages set archived_at=now() where membership_package_id=$1', [plan]), /record_archived|permission denied/);
  });
  await test('inactive catalog rejects edits, stale reactivation and physical deletion', async () => {
    await assert.rejects(as(owner, `update membership_packages set name='Changed' where membership_package_id=$1`, [plan]), /record_archived/);
    await assert.rejects(as(owner, `update membership_packages set name='Stale',is_active=true where membership_package_id=$1`, [plan]), /record_archived/);
    for (const [table, key, id] of [['membership_packages','membership_package_id',plan],['amenities','amenity_id',amenity],['discounts','discount_id',code]])
      await assert.rejects(as(owner, `delete from ${table} where ${key}=$1`, [id]), /permission denied/);
  });
  await test('direct deactivation is retained with reason and server timestamp', async () => {
    await as(owner, 'update amenities set is_active=false where amenity_id=$1', [amenity]);
    const row = (await list(owner, 'amenity')).rows.find(r => r.record_id === String(amenity));
    assert.ok(row.archived_at); assert.equal(row.archived_by, owner); assert.equal(row.reason, 'Deactivated amenity');
    await assert.rejects(as(owner, `select set_amenity_status($1,'unavailable','Archived asset',null)`, [amenity]), /record_archived/);
  });
  await test('all four types appear; filters combine using gym calendar dates and literal reason text', async () => {
    await as(owner, 'select archive_account($1,$2)', [desk, 'Duplicate [100%] account']);
    await archive('code', code, 'Deactivated code');
    const all = (await list()).rows;
    for (const type of ['account','plan','code','amenity']) assert.ok(all.some(r => r.record_type === type));
    const date = (await db.query(`select (now() at time zone 'Asia/Manila')::date::text archive_day`)).rows[0].archive_day;
    const filtered = (await list(owner, 'account', 'deleted', date, date, '[100%]')).rows;
    assert.equal(filtered.length, 1); assert.equal(filtered[0].record_id, desk);
    assert.equal((await list(owner, 'account', 'inactive', date, date, '[100%]')).rowCount, 0);
    assert.equal((await list(owner, '', '', date, date)).rows.some(r => r.record_id === String(legacyPlan) && r.record_type === 'plan'), false);
  });
  await test('expired codes are archived automatically, immutable and not restorable', async () => {
    const row = (await list(owner, 'code', 'expired')).rows.find(r => r.record_id === String(expired));
    assert.equal(row.can_restore, false); assert.equal(row.reason, 'Expired code'); assert.ok(row.archived_at);
    await assert.rejects(restore('code', expired), /record_not_restorable/);
    await assert.rejects(as(owner, `update discounts set expires_at=now()+interval '30 days' where discount_id=$1`, [expired]), /record_archived/);
  });
  await test('restoring clears archive metadata and records original reason in audit', async () => {
    await restore('plan', plan);
    const row = (await db.query('select is_active,archived_at,archived_by,archive_reason from membership_packages where membership_package_id=$1', [plan])).rows[0];
    assert.deepEqual(row, { is_active: true, archived_at: null, archived_by: null, archive_reason: null });
    assert.equal((await db.query(`select details->>'reason' reason from audit_log where action='catalog.restore' and entity_id=$1`, [String(plan)])).rows[0].reason, 'Retired gym plan');
    await assert.rejects(restore('plan', plan), /not_archived/);
    await as(owner, `update membership_packages set price=1100 where membership_package_id=$1`, [plan]);
    for (const [type, id] of [['amenity',amenity],['code',code],['account',desk]]) await restore(type, id);
  });
  await test('ordinary inactive accounts appear with audited date and restore as active', async () => {
    await as(owner, 'select set_user_active($1,false)', [desk]);
    const row = (await list(owner, 'account', 'inactive')).rows.find(r => r.record_id === desk);
    assert.ok(row.archived_at); assert.equal(row.archived_by, owner); assert.equal(row.reason, 'Deactivated account');
    await restore('account', desk);
  });
  await test('pagination is stable, bounded, and invalid filters fail', async () => {
    const first = (await list(owner, '', '', null, null, '', 0, 1)).rows[0];
    const second = (await list(owner, '', '', null, null, '', 1, 1)).rows[0];
    assert.notEqual(`${first.record_type}:${first.record_id}`, `${second.record_type}:${second.record_id}`);
    for (const values of [['unknown'],['','bogus'],['','','2026-10-20','2026-10-19'],['','',null,null,'',-1],['','',null,null,'',0,102]]) await assert.rejects(list(owner,...values), /bad_archive_filter/);
  });
  await test('archived accounts reject role and coach profile changes until restored', async () => {
    const trainer = await user('coach');
    await as(owner, 'select archive_account($1,$2)', [trainer, 'Departed coach']);
    await assert.rejects(as(owner, `select assign_role($1,'employee')`, [trainer]), /record_archived/);
    await assert.rejects(as(owner, `select revoke_role($1,'coach')`, [trainer]), /record_archived/);
    await assert.rejects(as(owner, `update coaches set bio='Stale edit' where user_id=$1`, [trainer]), /record_archived/);
    await restore('account', trainer);
    await as(owner, `update coaches set bio='Editable again' where user_id=$1`, [trainer]);
  });
  await test('new inactive records get current metadata; clients cannot forge it', async () => {
    await assert.rejects(as(owner, `insert into amenities(name,price,is_active,archived_at,archived_by,archive_reason) values('Forged inactive',0,false,'2000-01-01',$1,'Forged') returning *`, [member]), /permission denied/);
    const row = (await as(owner, `insert into amenities(name,price,is_active) values('Created inactive',0,false) returning *`)).rows[0];
    assert.equal(row.archived_by, owner); assert.equal(row.archive_reason, 'Created inactive'); assert.ok(row.archived_at.getFullYear() > 2000);
    await as(owner, `update membership_packages set archived_at='2000-01-01',archived_by=$1,archive_reason='Forged' where membership_package_id=$2`, [member, plan]);
    const active = (await db.query('select archived_at,archived_by,archive_reason from membership_packages where membership_package_id=$1', [plan])).rows[0];
    assert.deepEqual(active, { archived_at: null, archived_by: null, archive_reason: null });
  });
  await test('inactive admin cannot list, restore or write gym assets', async () => {
    const other = await user('admin'); await as(owner, 'select archive_account($1,$2)', [other, 'Departed admin']);
    await assert.rejects(list(other), /forbidden/); await assert.rejects(restore('plan', legacyPlan, other), /forbidden/);
    await db.query('grant select,insert,update,delete on storage.objects to authenticated');
    await assert.rejects(as(other, `insert into storage.objects(bucket_id,name) values('gym-assets','blocked.png')`), /row-level security/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Archive catalog: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
