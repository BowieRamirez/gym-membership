// 0132 (owner, 2026-11-21): an archived account's chats leave everyone's Messages list (coach chats and the desk inbox);
// the rows are kept, and restoring the account brings them back.
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
const user = async (role, name) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`ca-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin', 'Sys Admin'), desk = await user('employee', 'Desk Dana'), coach = await user('coach', 'Coach Kai');
const gone = await user('member', 'Gone Member'), stays = await user('member', 'Stays Member');
for (const m of [gone, stays]) {
  await db.query(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end,status) values($1,$2,now()-interval '3 days',now()-interval '3 days'+interval '1 hour','completed')`, [coach, m]);
  await db.query(`insert into messages(coach_id,member_id,sender_id,body) values($1,$2,$2,'hi coach')`, [coach, m]);
  await db.query(`insert into support_threads(member_id,queue) values($1,'front_desk')`, [m]);
  await db.query(`insert into support_messages(member_id,sender_id,body,queue) values($1,$1,'hi desk','front_desk')`, [m]);
}
const coachSees = async () => (await as(coach, 'select * from chat_threads()')).filter(t => t.kind === 'coach').map(t => t.member_id);
const deskSees = async () => (await as(desk, 'select * from chat_threads()')).filter(t => t.kind === 'support').map(t => t.member_id);

try {
  await test('before: the coach and the desk see both members', async () => {
    assert.deepEqual([(await coachSees()).includes(gone), (await deskSees()).includes(gone)], [true, true]);
  });
  await test('archived: the account leaves the coach list and the desk inbox; the other member stays', async () => {
    await as(admin, 'select archive_account($1,$2)', [gone, 'Test account']);
    assert.deepEqual([(await coachSees()).includes(gone), (await deskSees()).includes(gone)], [false, false]);
    assert.deepEqual([(await coachSees()).includes(stays), (await deskSees()).includes(stays)], [true, true]);
    assert.equal((await db.query('select count(*)::int n from messages where member_id=$1', [gone])).rows[0].n, 1, 'messages are kept');
  });
  await test('restored: the chats come back', async () => {
    await as(admin, 'select restore_account($1)', [gone]);
    assert.deepEqual([(await coachSees()).includes(gone), (await deskSees()).includes(gone)], [true, true]);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Chat archived: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
