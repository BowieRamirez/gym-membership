// Phase 13 (E10): Damage reports after a session or from desk, reviewed by admins, no automatic charge.
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
    values($1,$2,$3) returning id`, [`dr-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const admin = await user('admin', 'Sys Admin');
const desk = await user('employee', 'Desk Dana');
const coach1 = await user('coach', 'Coach Kai');
const coach2 = await user('coach', 'Coach Lena');
const member = await user('member', 'Mia Santos');
const ring = (await db.query(`insert into amenities(name,price,duration_days) values('Boxing Ring',300,30) returning amenity_id`)).rows[0].amenity_id;

const ses = (await db.query(
  `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
   values($1,$2, now() - interval '2 hours', now() - interval '1 hour', 'completed')
   returning training_session_id`,
  [coach1, member]
)).rows[0].training_session_id;

try {
  await test('coach reports on own completed session; another coach cannot', async () => {
    await assert.rejects(as(coach2, 'select report_damage($1,$2,$3,null)', [ses, ring, 'Broken rope']), /forbidden/);
    const id = (await as(coach1, 'select report_damage($1,$2,$3,null) id', [ses, ring, 'Broken rope']))[0].id;
    assert.ok(id > 0);
    const notif = (await db.query(`select title, body from user_notifications where user_id=$1 order by notification_id desc limit 1`, [admin])).rows[0];
    assert.equal(notif.title, 'Damage report filed');
    assert.equal(notif.body, 'Broken rope');
  });

  await test('admin closes with a note and sets amenity to maintenance; no payment or refund row made', async () => {
    const payBefore = (await db.query('select count(*)::int n from payments')).rows[0].n;
    const refBefore = (await db.query('select count(*)::int n from refund_requests')).rows[0].n;
    const id = (await as(desk, 'select report_damage(null,$1,$2,null) id', [ring, 'Torn mat']))[0].id;

    await assert.rejects(as(desk, `select review_damage($1,'closed','Fixed',false)`, [id]), /forbidden/);
    await as(admin, `select review_damage($1,'closed','Sent for repair',true)`, [id]);

    const rep = (await db.query('select status, review_note from damage_reports where damage_report_id=$1', [id])).rows[0];
    assert.equal(rep.status, 'closed');
    assert.equal(rep.review_note, 'Sent for repair');

    const am = (await db.query('select status from amenities where amenity_id=$1', [ring])).rows[0];
    assert.equal(am.status, 'unavailable');

    const payAfter = (await db.query('select count(*)::int n from payments')).rows[0].n;
    const refAfter = (await db.query('select count(*)::int n from refund_requests')).rows[0].n;
    assert.equal(payAfter, payBefore);
    assert.equal(refAfter, refBefore);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nDamageReports: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
