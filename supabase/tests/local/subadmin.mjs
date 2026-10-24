// Phase 13 (D15): the admin is the super admin; a sub-admin does the front desk's work plus some admin pages, and the server refuses
// everything else even when called directly: money reversals, gym settings, the audit log, roles, archiving and deleting, and any
// action on a front-desk employee, sub-admin or super admin.
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
async function user(role = 'member') {
  const id = (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,'{"full_name":"Sub Person"}',$2) returning id`,
    [`p13-${++serial}@test.io`, JSON.stringify({ role: role === 'subadmin' ? 'member' : role })])).rows[0].id;
  if (role === 'subadmin') {
    await db.query(`delete from user_roles where user_id=$1`, [id]);
    await db.query(`insert into user_roles(user_id, role_id) select $1, role_id from roles where name='subadmin'`, [id]);
  }
  return id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const refused = p => assert.rejects(p, /forbidden|42501|permission denied/);
const isActive = async id => (await db.query('select is_active from profiles where id=$1', [id])).rows[0].is_active;

const superAdmin = await user('admin'), sub = await user('subadmin'), desk = await user('employee'), coach = await user('coach'), member = await user();
const other = await user('subadmin');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P13 Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;

try {
  await test('a sub-admin is staff and holds exactly the approved permissions', async () => {
    const yes = ['payments:verify', 'payments:record', 'check_ins:record', 'amenities:set_status', 'support:reply', 'reports:cash', 'reports:revenue',
      'catalog:manage', 'discounts:manage', 'users:manage'];
    const no = ['payments:void', 'roles:manage', 'gym_settings:manage', 'audit:read'];
    assert.equal((await as(sub, 'select public.is_staff() s'))[0].s, true);
    for (const p of yes) assert.equal((await as(sub, 'select public.has_permission($1) h', [p]))[0].h, true, p);
    for (const p of no) assert.equal((await as(sub, 'select public.has_permission($1) h', [p]))[0].h, false, p);
  });
  await test('the super admin still holds every admin permission', async () => {
    for (const p of ['payments:void', 'roles:manage', 'gym_settings:manage', 'audit:read', 'users:manage', 'catalog:manage'])
      assert.equal((await as(superAdmin, 'select public.has_permission($1) h', [p]))[0].h, true, p);
  });
  await test('a sub-admin sees everyone\'s roles on the Users page', async () => {
    assert.ok((await as(sub, 'select count(*)::int n from user_roles'))[0].n >= 5);
  });
  await test('a sub-admin deactivates and reactivates members and coaches', async () => {
    for (const id of [member, coach]) {
      await as(sub, 'select set_user_active($1,false)', [id]);
      assert.equal(await isActive(id), false);
      await as(sub, 'select set_user_active($1,true)', [id]);
      assert.equal(await isActive(id), true);
    }
  });
  await test('a sub-admin cannot deactivate a front-desk employee, another sub-admin or the super admin', async () => {
    for (const id of [desk, other, superAdmin]) { await refused(as(sub, 'select set_user_active($1,false)', [id])); assert.equal(await isActive(id), true); }
  });
  await test('only the super admin hands out roles, including sub-admin', async () => {
    await refused(as(sub, `select assign_role($1,'coach')`, [member]));
    await refused(as(sub, `select assign_role($1,'subadmin')`, [member]));
    await as(superAdmin, `select assign_role($1,'subadmin')`, [member]);
    assert.ok((await db.query(`select 1 from user_roles join roles using(role_id) where user_id=$1 and name='subadmin'`, [member])).rowCount);
    await as(superAdmin, `select revoke_role($1,'subadmin')`, [member]);
  });
  await test('a sub-admin edits packages but cannot archive or delete them', async () => {
    await as(sub, 'update membership_packages set price=1100 where membership_package_id=$1', [pack]);
    assert.equal(Number((await db.query('select price from membership_packages where membership_package_id=$1', [pack])).rows[0].price), 1100);
    await refused(as(sub, `select archive_catalog_record('plan',$1,'old')`, [pack]));
    await refused(as(sub, 'delete from membership_packages where membership_package_id=$1', [pack]));
    assert.ok((await db.query('select 1 from membership_packages where membership_package_id=$1', [pack])).rowCount);
  });
  await test('a sub-admin cannot delete accounts, restore from Archive, change gym settings or read the audit log', async () => {
    await refused(as(sub, `select archive_account($1,'left the gym')`, [member]));
    await refused(as(sub, `select restore_archived_record('account',$1)`, [member]));
    await as(sub, `update gym_settings set gym_name='Hacked'`);
    assert.notEqual((await db.query('select gym_name from gym_settings')).rows[0].gym_name, 'Hacked');
    assert.equal((await as(sub, 'select count(*)::int n from audit_log'))[0].n, 0);
  });
  await test('a sub-admin cannot void a payment or decide a refund, but reads revenue', async () => {
    await refused(as(sub, `select void_payment(1,'mistake')`));
    await refused(as(sub, `select decide_membership_refund(1,true,'ok')`));
    await as(sub, `select * from revenue_lines(current_date, current_date)`);
  });
  // 0037: super admin accounts are fixed from the app; only the database owner (dashboard) can change them
  const secondSuper = await user('admin');
  await test('nobody can change a super admin\'s roles, deactivate or delete them from the app, not even another super admin', async () => {
    const protectedErr = /super_admin_protected/;
    await assert.rejects(as(superAdmin, `select assign_role($1,'coach')`, [secondSuper]), protectedErr);
    await assert.rejects(as(superAdmin, `select revoke_role($1,'admin')`, [secondSuper]), protectedErr);
    await assert.rejects(as(superAdmin, `select set_user_active($1,false)`, [secondSuper]), protectedErr);
    await assert.rejects(as(superAdmin, `select archive_account($1,'gone')`, [secondSuper]), protectedErr);
    assert.equal(await isActive(secondSuper), true);
    assert.equal((await db.query(`select count(*)::int n from user_roles where user_id=$1`, [secondSuper])).rows[0].n, 1);
  });
  await test('the super admin adds a sub-admin directly', async () => {
    const id = (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values('p13-new-sub@test.io','{"full_name":"New Sub"}','{"role":"subadmin"}') returning id`)).rows[0].id;
    assert.deepEqual((await db.query(`select name from user_roles join roles using(role_id) where user_id=$1`, [id])).rows.map(r => r.name), ['subadmin']);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exitCode = 1;
}
