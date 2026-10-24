import assert from 'node:assert/strict';
import pg from 'pg';
import { start } from './harness.mjs';

let legacy;
const { server, client: db } = await start({ beforeMigration: async (client, file) => {
  if (file === '0023_account_archive.sql') {
    legacy = (await client.query(`insert into auth.users(email,raw_user_meta_data) values('legacy@archive.io','{"full_name":"Legacy Person"}') returning id`)).rows[0].id;
    await client.query('update profiles set is_active=false where id=$1', [legacy]);
  }
} });
let passed = 0, failed = 0, sequence = 0;
const clients = [];
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (error) { failed++; console.log('  FAIL', name, error.message); }
}
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,'{"full_name":"Archive Person"}',$2) returning id`,
    [`archive-${++sequence}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function begin(client, uid) {
  await client.query('begin');
  await client.query('set local role authenticated');
  await client.query(`select set_config('request.jwt.claim.sub',$1,true)`, [uid]);
}
async function as(uid, sql, values = []) {
  await begin(db, uid);
  try { const value = await db.query(sql, values); await db.query('commit'); return value; }
  catch (error) { await db.query('rollback'); throw error; }
}
async function connection() {
  const client = new pg.Client({ host: db.connectionParameters.host, port: db.connectionParameters.port,
    user: 'postgres', password: 'pw', database: 'postgres' });
  await client.connect(); clients.push(client); return client;
}
async function waitBlocked(client) {
  const pid = client.processID;
  for (let n = 0; n < 100; n++) {
    if ((await db.query('select cardinality(pg_blocking_pids($1)) n', [pid])).rows[0].n > 0) return;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error('Expected concurrent operation to wait for a lock');
}
const archive = (actor, id, reason = 'No longer needed') => as(actor, 'select archive_account($1,$2)', [id, reason]);
const owner = await user('admin');
const desk = await user('employee');
const coach = await user('coach');
const member = await user();
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('Archive monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;

try {
  await test('existing inactive profiles keep unknown deletion metadata', async () => {
    const row = (await db.query('select is_active,deleted_at,deleted_by,deletion_reason from profiles where id=$1', [legacy])).rows[0];
    assert.deepEqual(row, { is_active: false, deleted_at: null, deleted_by: null, deletion_reason: null });
  });
  for (const [name, actor] of [['member', member], ['coach', coach], ['front desk', desk]]) {
    await test(`${name} cannot delete accounts`, async () => assert.rejects(archive(actor, member), /forbidden/));
    await test(`${name} cannot restore accounts`, async () => assert.rejects(as(actor, 'select restore_account($1)', [member]), /forbidden/));
  }
  await test('a nonadmin with users:manage still cannot delete', async () => {
    await db.query(`insert into role_permissions select r.role_id,p.permission_id from roles r cross join permissions p where r.name='employee' and p.resource='users' and p.action='manage'`);
    try { await assert.rejects(archive(desk, member), /forbidden/); }
    finally { await db.query(`delete from role_permissions where role_id=(select role_id from roles where name='employee') and permission_id=(select permission_id from permissions where resource='users' and action='manage')`); }
  });
  await test('blank, null and overlong deletion reasons are rejected', async () => {
    for (const reason of ['', '   ', '\r\n\t', null, 'a'.repeat(501)]) await assert.rejects(archive(owner, member, reason), /bad_archive_reason/);
  });
  await test('self deletion is rejected and last admin stays active', async () => {
    await assert.rejects(archive(owner, owner), /self_archive/);
    assert.equal((await db.query('select is_active from profiles where id=$1', [owner])).rows[0].is_active, true);
  });
  await test('only-admin role cannot be removed through a delegated manager', async () => {
    await db.query(`insert into role_permissions select r.role_id,p.permission_id from roles r cross join permissions p where r.name='employee' and p.resource='roles' and p.action='manage'`);
    try { await assert.rejects(as(desk, `select revoke_role($1,'admin')`, [owner]), /last_active_admin/); }
    finally { await db.query(`delete from role_permissions where role_id=(select role_id from roles where name='employee') and permission_id=(select permission_id from permissions where resource='roles' and action='manage')`); }
  });
  await test('pending payment blocks deletion without cancelling it', async () => {
    const target = await user();
    const payment = (await as(target, 'select * from avail_membership($1)', [pack])).rows[0];
    await assert.rejects(archive(owner, target), /account_has_commitments/);
    assert.equal((await db.query('select status from payments where payment_id=$1', [payment.payment_id])).rows[0].status, 'pending');
  });
  await test('active hires block deletion for either participant', async () => {
    const target = await user();
    await db.query('insert into coach_hires(member_id,coach_id) values($1,$2)', [target, coach]);
    await assert.rejects(archive(owner, target), /account_has_commitments/);
    await assert.rejects(archive(owner, coach), /account_has_commitments/);
  });
  await test('scheduled sessions block deletion even when their date has passed', async () => {
    const target = await user(); const trainer = await user('coach');
    await db.query(`insert into training_sessions(member_id,coach_id,scheduled_start,scheduled_end) values($1,$2,now()-interval '2 hours',now()-interval '1 hour')`, [target, trainer]);
    await assert.rejects(archive(owner, target), /account_has_commitments/);
    await assert.rejects(archive(owner, trainer), /account_has_commitments/);
  });
  await test('soft deletion preserves identity/history and records trimmed reason, actor and time', async () => {
    await db.query(`insert into audit_log(actor_id,action,entity,entity_id) values($1::uuid,'test.history','profiles',$1::uuid::text)`, [member]);
    await archive(owner, member, '  Duplicate account  ');
    const row = (await db.query('select * from profiles where id=$1', [member])).rows[0];
    assert.equal(row.is_active, false); assert.equal(row.deleted_by, owner);
    assert.ok(row.deleted_at); assert.equal(row.deletion_reason, 'Duplicate account');
    assert.equal((await db.query('select count(*)::int n from auth.users where id=$1', [member])).rows[0].n, 1);
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='test.history' and actor_id=$1`, [member])).rows[0].n, 1);
    assert.equal((await db.query(`select details->>'reason' reason from audit_log where action='user.archive' and entity_id=$1`, [member])).rows[0].reason, 'Duplicate account');
  });
  await test('existing archived identity is refused protected reads and RPC writes', async () => {
    assert.equal((await as(member, 'select * from membership_packages')).rowCount, 0);
    assert.equal((await as(member, 'select * from profiles')).rowCount, 1); // own status remains readable for sign-in handling
    await assert.rejects(as(member, 'select avail_membership($1)', [pack]), /forbidden/);
    await assert.rejects(as(member, 'select mark_password_changed()'), /forbidden/);
    await assert.rejects(as(member, 'select touch_last_seen()'), /forbidden/);
  });
  await test('direct deletion metadata edits and physical profile deletion are refused', async () => {
    await assert.rejects(as(owner, `update profiles set deleted_at=null where id=$1`, [member]), /permission denied/);
    await assert.rejects(as(owner, 'delete from profiles where id=$1', [member]), /permission denied/);
    await assert.rejects(as(owner, 'select require_archive_admin()'), /permission denied/);
    await assert.rejects(as(owner, 'select guard_account_commitment()'), /permission denied/);
  });
  await test('duplicate deletion is rejected without replacing original evidence', async () => {
    await assert.rejects(archive(owner, member, 'New reason'), /already_processed/);
    assert.equal((await db.query('select deletion_reason from profiles where id=$1', [member])).rows[0].deletion_reason, 'Duplicate account');
    assert.equal((await db.query(`select count(*)::int n from audit_log where action='user.archive' and entity_id=$1`, [member])).rows[0].n, 1);
  });
  await test('normal reactivation cannot bypass restore; restore preserves archive audit evidence', async () => {
    await assert.rejects(as(owner, 'select set_user_active($1,true)', [member]), /account_archived/);
    await as(owner, 'select restore_account($1)', [member]);
    const row = (await db.query('select is_active,deleted_at from profiles where id=$1', [member])).rows[0];
    assert.deepEqual(row, { is_active: true, deleted_at: null });
    assert.equal((await db.query(`select details->>'reason' reason from audit_log where action='user.restore' and entity_id=$1`, [member])).rows[0].reason, 'Duplicate account');
    await assert.rejects(as(owner, 'select restore_account($1)', [member]), /not_archived/);
  });
  await test('archive winning a race prevents a new pending payment', async () => {
    const target = await user(); const a = await connection(), b = await connection();
    await begin(a, owner); await a.query('select archive_account($1,$2)', [target, 'Race check']);
    await begin(b, target);
    const pending = b.query('select avail_membership($1)', [pack]).then(value => ({ value }), error => ({ error }));
    await waitBlocked(b); await a.query('commit');
    const result = await pending; assert.match(result.error?.message ?? '', /account_inactive/);
    await b.query('rollback');
    assert.equal((await db.query('select count(*)::int n from payments where member_id=$1', [target])).rows[0].n, 0);
  });
  await test('payment winning a race blocks archive after waiting', async () => {
    const target = await user(); const a = await connection(), b = await connection();
    await begin(a, target); await a.query('select avail_membership($1)', [pack]);
    await begin(b, owner);
    const pending = b.query('select archive_account($1,$2)', [target, 'Race check']).then(value => ({ value }), error => ({ error }));
    await waitBlocked(b); await a.query('commit');
    const result = await pending; assert.match(result.error?.message ?? '', /account_has_commitments/);
    await b.query('rollback');
    assert.equal((await db.query('select is_active from profiles where id=$1', [target])).rows[0].is_active, true);
  });
  // 0037: a super admin cannot be archived from the app at all, so two admins can no longer race to remove each other
  await test('admins cannot archive one another', async () => {
    const other = await user('admin');
    await assert.rejects(as(owner, 'select archive_account($1,$2)', [other, 'Race check']), /super_admin_protected/);
    await assert.rejects(as(other, 'select archive_account($1,$2)', [owner, 'Race check']), /super_admin_protected/);
    for (const id of [owner, other]) assert.equal((await db.query('select is_active from profiles where id=$1', [id])).rows[0].is_active, true);
  });
} finally {
  for (const client of clients) { await client.query('rollback').catch(() => {}); await client.end(); }
  await db.end(); await server.stop();
}
console.log(`\nAccount archive: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
