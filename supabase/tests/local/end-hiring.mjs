// Phase 11 (E8): Hiring and Time requests removed; messages follow sessions; old hires end with the member's term.
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
async function asAnon(sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role anon');
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(role = 'member', name = 'Test') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`eh-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const desk = await user('employee', 'Desk');
const coach = await user('coach', 'Coach Kai');
const member = await user('member', 'Mia');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days,includes_coach) values('EH Pack',1000,30,true) returning membership_package_id`)).rows[0].membership_package_id;

// Give member an active membership
const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);

try {
  await test('hire and time-request functions are gone for anon and authenticated', async () => {
    const calls = [
      ['select hire_coach($1)', [coach]],
      ['select answer_hire(1, true)', []],
      ['select cancel_hire_request(1)', []],
      ['select request_time($1, now(), now() + interval \'1 hour\', null)', [coach]],
      ['select respond_time_request(1, true)', []],
      ['select request_member_time($1, now(), now() + interval \'1 hour\')', [member]],
      ['select request_session($1, now(), now() + interval \'1 hour\', null, null)', [coach]],
      ['select propose_time(1, now(), now() + interval \'1 hour\', null, \'x\')', []],
      ['select accept_proposal(1, 1)', []],
      ['select request_another_time(1, now(), now() + interval \'1 hour\', null, null)', []],
      ['select decline_time_request(1, \'x\')', []],
      ['select cancel_time_request(1)', []],
    ];
    for (const [sql, args] of calls) {
      await assert.rejects(as(member, sql, args), /does not exist/);
      await assert.rejects(asAnon(sql, args), /does not exist/);
    }
  });

  await test('messaging allowed with an upcoming session and within 7 days after it ends', async () => {
    const m2 = await user('member', 'Leo');
    const p2 = (await as(m2, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [p2.payment_id]);

    // Insert an upcoming session
    const s1 = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
       values($1, $2, now() + interval '1 day', now() + interval '1 day 1 hour', 'scheduled') returning training_session_id`,
      [coach, m2]
    )).rows[0].training_session_id;

    await as(m2, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$2,$3)', [coach, m2, 'See you tomorrow!']);
    await as(coach, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$1,$3)', [coach, m2, 'See you!']);
    const t = (await as(m2, 'select * from chat_threads()')).find(x => x.kind === 'coach' && x.coach_id === coach);
    assert.ok(t && t.can_send === true);

    // Move session to 6 days ago (completed) -> still within 7 days
    await db.query(
      `update training_sessions set scheduled_start = now() - interval '6 days 1 hour', scheduled_end = now() - interval '6 days', status = 'completed'
       where training_session_id = $1`,
      [s1]
    );
    await as(m2, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$2,$3)', [coach, m2, 'Thanks for the session!']);

    // Move session to 8 days ago -> refused
    await db.query(
      `update training_sessions set scheduled_start = now() - interval '8 days 1 hour', scheduled_end = now() - interval '8 days'
       where training_session_id = $1`,
      [s1]
    );
    await assert.rejects(as(m2, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$2,$3)', [coach, m2, 'Too late']), /row-level security/);
    const tAfter = (await as(m2, 'select * from chat_threads()')).find(x => x.kind === 'coach' && x.coach_id === coach);
    assert.ok(tAfter && tAfter.can_send === false);
    assert.equal((await as(m2, 'select can_use_chat_file($1, true) ok', [`${coach}/${m2}/a.png`]))[0].ok, false);
  });

  await test('an active old hire still messages until the member term ends, then ends on expiry', async () => {
    const m3 = await user('member', 'Nora');
    const p3 = (await as(m3, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [p3.payment_id]);

    const hireId = (await db.query(
      `insert into coach_hires(coach_id, member_id, status) values($1,$2,'active') returning coach_hire_id`,
      [coach, m3]
    )).rows[0].coach_hire_id;

    await as(m3, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$2,$3)', [coach, m3, 'Legacy hire still works']);

    // Expire Nora's term
    await db.query(`update membership_terms set ends_at = now() - interval '1 hour' where member_id = $1`, [m3]);
    await db.query('select expire_memberships()');

    const hire = (await db.query(`select status, ended_at from coach_hires where coach_hire_id = $1`, [hireId])).rows[0];
    assert.equal(hire.status, 'ended');
    assert.ok(hire.ended_at);
    await assert.rejects(as(m3, 'insert into messages(coach_id, member_id, sender_id, body) values($1,$2,$2,$3)', [coach, m3, 'After term']), /row-level security/);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nEndHiring: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
