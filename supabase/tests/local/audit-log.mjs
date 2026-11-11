// Phase 8 (0045, D7): new audit entries record the actor's role and address; the log is read a page at a time with filters,
// only by staff with audit:read; addresses older than 90 days are cleared, the rest of the entry stays.
import assert from 'node:assert/strict';
import { start } from './harness.mjs';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
async function as(uid, sql, args = [], headers = null) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    await db.query(`select set_config('request.jwt.claims', $1, true)`, [JSON.stringify({ sub: uid })]);
    if (headers !== null) await db.query(`select set_config('request.headers', $1, true)`, [headers]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function person(role, name) {
  return (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
    [`audit-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify(role ? { role } : {})])).rows[0].id;
}
const page = (uid, args = {}) => as(uid, `select * from audit_records(p_from=>$1, p_to=>$2, p_user=>$3, p_area=>$4, p_entity=>$5, p_offset=>$6, p_limit=>$7)`,
  [args.from ?? null, args.to ?? null, args.user ?? '', args.area ?? '', args.entity ?? '', args.offset ?? 0, args.limit ?? 11]);

const sysAdmin = await person('admin', 'Dana Reyes');
const subAdmin = await person('subadmin', 'Sam Villanueva');
const desk = await person('employee', 'Marco Santos');
const member = await person(null, 'Alex Tan');

try {
  await test('an audited action records the role at the time and the address Cloudflare passed', async () => {
    await as(sysAdmin, `select correct_name($1, 'Alex', 'Tan', 'Typo at the desk')`, [member],
      JSON.stringify({ 'cf-connecting-ip': '203.0.113.7', 'x-forwarded-for': '198.51.100.1, 10.0.0.1' }));
    const row = (await db.query(`select role, ip_address from audit_log where action='user.correct_name' order by audit_id desc limit 1`)).rows[0];
    assert.deepEqual(row, { role: 'admin', ip_address: '203.0.113.7' });
  });
  await test('without cf-connecting-ip the first forwarded address is used; no headers or bad headers give null, never an error', async () => {
    for (const [headers, ip] of [[JSON.stringify({ 'x-forwarded-for': ' 198.51.100.1 , 10.0.0.1' }), '198.51.100.1'], [null, null], ['not json', null], ['{}', null]]) {
      await as(sysAdmin, `select correct_name($1, 'Alex', 'Tan', 'Check')`, [member], headers);
      assert.equal((await db.query(`select ip_address from audit_log order by audit_id desc limit 1`)).rows[0].ip_address, ip);
    }
  });
  await test('an entry with no actor (cron or migration) has no role and no address', async () => {
    await db.query(`select audit('test.system', 'profiles', null)`);
    assert.deepEqual((await db.query(`select role, ip_address from audit_log order by audit_id desc limit 1`)).rows[0], { role: null, ip_address: null });
  });
  await test('the main role follows the app order: a System admin who is also a coach is admin', async () => {
    await db.query(`insert into user_roles(user_id, role_id) select $1, role_id from roles where name = 'coach'`, [sysAdmin]);
    assert.equal((await db.query('select main_role($1) r', [sysAdmin])).rows[0].r, 'admin');
    assert.equal((await db.query('select main_role($1) r', [member])).rows[0].r, 'member');
  });
  await test('only staff with audit:read can read the log; Admin, front desk, members and anonymous are refused', async () => {
    assert.ok((await page(sysAdmin)).length > 0);
    for (const uid of [subAdmin, desk, member]) await assert.rejects(page(uid), /forbidden/);
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query(`select * from audit_records()`), /permission denied/);
    await db.query('rollback');
  });
  await test('pages are newest first, 10 a page with an 11th row telling the screen there is more', async () => {
    for (let i = 0; i < 25; i++) await db.query(`select audit('test.paging', 'payments', $1)`, [String(i)]);
    const first = await page(sysAdmin, { area: 'test', entity: 'payments' });
    assert.equal(first.length, 11);
    assert.equal(first[0].entity_id, '24');
    const third = await page(sysAdmin, { area: 'test', entity: 'payments', offset: 20 });
    assert.deepEqual(third.map(r => r.entity_id), ['4', '3', '2', '1', '0']);
  });
  await test('filters: user name part, action area, object table, Manila dates; names of account objects are shown', async () => {
    const byName = await page(sysAdmin, { user: 'dana', area: 'user', entity: 'profiles' });
    assert.ok(byName.length >= 1 && byName.every(r => r.actor_name === 'Dana Reyes' && r.action.startsWith('user.')));
    assert.equal(byName[0].object_name, 'Alex Tan');
    await as(sysAdmin, `select assign_role($1, 'coach')`, [member]);
    const role = (await page(sysAdmin, { area: 'role', entity: 'user_roles' }))[0];
    assert.deepEqual([role.action, role.object_name, role.details.role], ['role.assign', 'Alex Tan', 'coach']);
    assert.ok((await page(sysAdmin, { user: 'system', area: 'test' })).every(r => r.actor_name === 'System'));
    assert.equal((await page(sysAdmin, { user: 'nobody by this name' })).length, 0);
    await db.query(`update audit_log set created_at = '2026-01-01T15:59:00Z' where action = 'test.system'`);   // 23:59 on 1 Jan in Manila
    assert.equal((await page(sysAdmin, { area: 'test', from: '2026-01-01', to: '2026-01-01' })).length, 1);
    assert.equal((await page(sysAdmin, { area: 'test', from: '2026-01-02', to: '2026-01-02' })).length, 0);
  });
  await test('bad filters are refused, and hostile text is only compared', async () => {
    for (const args of [{ offset: -1 }, { limit: 0 }, { limit: 1002 }, { from: '2026-02-01', to: '2026-01-01' }, { user: 'x'.repeat(101) }, { area: 'x'.repeat(41) }])
      await assert.rejects(page(sysAdmin, args), /bad_audit_filter/);
    assert.equal((await page(sysAdmin, { user: `'; drop table audit_log; --`, area: `%`, entity: `_` })).length, 0);
    assert.ok((await db.query(`select to_regclass('public.audit_log') t`)).rows[0].t);
  });
  await test('addresses older than 90 days are cleared nightly and the rest of the entry stays', async () => {
    await db.query(`update audit_log set ip_address = '192.0.2.1', created_at = now() - interval '91 days' where action = 'test.paging' and entity_id = '0'`);
    await db.query(`update audit_log set ip_address = '192.0.2.2', created_at = now() - interval '89 days' where action = 'test.paging' and entity_id = '1'`);
    assert.equal((await db.query('select clear_old_audit_ips() n')).rows[0].n, 1);
    const rows = (await db.query(`select entity_id, ip_address, action from audit_log where action = 'test.paging' and entity_id in ('0','1') order by entity_id`)).rows;
    assert.deepEqual(rows, [{ entity_id: '0', ip_address: null, action: 'test.paging' }, { entity_id: '1', ip_address: '192.0.2.2', action: 'test.paging' }]);
    for (const uid of [sysAdmin, desk]) await assert.rejects(as(uid, 'select clear_old_audit_ips()'), /permission denied/);
  });
  await test('the app still cannot write or change the log directly', async () => {
    await assert.rejects(as(sysAdmin, `insert into audit_log(action, entity) values('fake', 'x')`), /permission denied|row-level/);
    await assert.rejects(as(sysAdmin, `update audit_log set ip_address = null`), /permission denied|row-level/);
    await assert.rejects(as(sysAdmin, `select main_role($1)`, [member]), /permission denied/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exitCode = 1;
}
