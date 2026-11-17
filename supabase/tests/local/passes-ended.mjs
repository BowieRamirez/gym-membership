// Owner, 2026-11-17: amenity bookings are per session only. 0066 (G19) stopped selling passes but let ones already bought keep
// booking until they ended; 0089 ends those now, so no booking says "Uses your pass" any more. Sessions are untouched.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

let member, pass, session;
const { server, client: db } = await start({ beforeMigration: async (c, f) => {
  if (!f.startsWith('0089')) return;
  member = (await c.query(`insert into auth.users(email,raw_user_meta_data) values('pe@test.io','{"full_name":"Pass Person","terms_version":"2026-11-15"}') returning id`)).rows[0].id;
  const ring = (await c.query(`insert into amenities(name,price) values('Ring',300) returning amenity_id id`)).rows[0].id;
  // a pass bought before 0066 (the database owner may still write one, as the old sale did)
  pass = (await c.query(`insert into user_amenities(member_id,amenity_id,status,expires_at) values($1,$2,'active',now()+interval '1 day') returning user_amenity_id id`, [member, ring])).rows[0].id;
  await c.query(`update user_amenities set is_session = false where user_amenity_id = $1`, [pass]);
  session = (await c.query(`insert into user_amenities(member_id,amenity_id,status) values($1,$2,'active') returning user_amenity_id id`, [member, ring])).rows[0].id;
  await c.query(`update user_amenities set is_session = true, expires_at = null where user_amenity_id = $1`, [session]);
} });
let passed = 0, failed = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  await test('a timed pass still active is ended, so it books nothing', async () => {
    assert.equal((await db.query('select status from user_amenities where user_amenity_id = $1', [pass])).rows[0].status, 'expired');
  });
  await test('an unused session stays usable', async () => {
    assert.equal((await db.query('select status from user_amenities where user_amenity_id = $1', [session])).rows[0].status, 'active');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Passes ended: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
