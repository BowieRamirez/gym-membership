// 0038: Supabase Auth saves app_metadata (the role) a moment after inserting the user, and 0011's trigger applies it then. 0036 added
// the subadmin role but not to that trigger's list, so a sub-admin added through create-user came out as a member. This checks the
// two-step creation for every staff role and that 0038 repairs accounts already created that way.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

// the way Supabase Auth creates a user: the row first, the role a moment later
async function twoStep(db, email, role) {
  const id = (await db.query(`insert into auth.users(email,raw_user_meta_data) values($1,'{"first_name":"Two","last_name":"Step"}') returning id`, [email])).rows[0].id;
  await db.query(`update auth.users set raw_app_meta_data = raw_app_meta_data || jsonb_build_object('role', $2::text) where id=$1`, [id, role]);
  return id;
}
let broken;
const { server, client: db } = await start({ beforeMigration: async (db, name) => {
  if (name.startsWith('0038_')) broken = await twoStep(db, 'p38-broken@test.io', 'subadmin');
} });
let passed = 0, failed = 0, serial = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const rolesOf = async id => (await db.query('select r.name from user_roles join roles r using(role_id) where user_id=$1 order by 1', [id])).rows.map(r => r.name);
const has = async (table, id) => (await db.query(`select 1 from ${table} where user_id=$1`, [id])).rowCount > 0;

try {
  await test('before 0038, a sub-admin created in two steps stayed a member (the bug)', async () => {
    assert.deepEqual(await rolesOf(broken), ['subadmin'], 'the migration should have repaired it');
  });
  for (const role of ['subadmin', 'employee', 'coach']) {
    await test(`a ${role} created in two steps gets that role only, with no member row or QR code`, async () => {
      const id = await twoStep(db, `p38-${++serial}@test.io`, role);
      assert.deepEqual(await rolesOf(id), [role]);
      assert.equal(await has('members', id), false);
      assert.equal(await has('member_codes', id), false);
      assert.equal(await has('coaches', id), role === 'coach');
    });
  }
  await test('a member created in two steps stays a member with a QR code', async () => {
    const id = await twoStep(db, `p38-${++serial}@test.io`, 'member');
    assert.deepEqual(await rolesOf(id), ['member']);
    assert.equal(await has('member_codes', id), true);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exitCode = 1;
}
