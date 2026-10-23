// Phase 7 (D7/D8): structured training requests. A member request may name an amenity; the coach approves, declines with a
// reason, or proposes another time (24 h or until its start); the member accepts that exact proposal, asks for another time,
// or declines. Confirming creates the session and, with an amenity, its reservation in one transaction. Every change is
// recorded in an append-only history. A session stops blocking new requests once its end time has passed (D7).
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
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p7s-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const desk = await user('employee');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P7S Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
const [court, ring] = (await db.query(`insert into amenities(name,price,duration_days) values('P7S Court',300,30),('P7S Ring',300,30) returning amenity_id`)).rows.map(r => r.amenity_id);
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const other = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 4)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
async function pair() {
  const coach = await user('coach'), member = await user();
  const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
  await as(member, 'select hire_coach($1)', [coach]);
  await as(coach, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [coach, at('06:00'), at('22:00')]);
  return { coach, member };
}
const ask = (p, from, to, amenity = court) => as(p.member, 'select request_session($1,$2,$3,$4,null) id', [p.coach, at(from), at(to), amenity]).then(r => r[0].id);
const propose = (p, id, from, to, amenity = court, reason = 'Court is busier then', d = day) =>
  as(p.coach, 'select propose_time($1,$2,$3,$4,$5)', [id, at(from, d), at(to, d), amenity, reason]);
const req = async id => (await db.query('select * from time_requests where time_request_id=$1', [id])).rows[0];
const accept = (p, id, version) => as(p.member, 'select accept_proposal($1,$2) r', [id, version]).then(r => r[0].r);
const booked = async p => (await db.query(`select r.amenity_id, r.coach_id, to_char(r.starts_at at time zone 'Asia/Manila','HH24:MI') s, r.training_session_id is not null linked
  from reservations r where r.member_id=$1 and r.status='confirmed'`, [p.member])).rows;

try {
  await test('a member request can name an amenity; with one it must fit the grid and gym hours', async () => {
    const p = await pair();
    const id = await ask(p, '10:00', '11:00');
    const r = await req(id);
    assert.equal(r.status, 'pending'); assert.equal(String(r.amenity_id), String(court));
    await as(p.member, 'select cancel_time_request($1)', [id]);
    await assert.rejects(ask(p, '10:15', '11:00'), /off_grid/);
    await assert.rejects(ask(p, '21:30', '22:30'), /outside_gym_hours/);
  });
  await test('only the coach proposes, with a reason; the proposal expires after 24 h or at its start', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00');
    await assert.rejects(as(p.member, 'select propose_time($1,$2,$3,$4,$5)', [id, at('12:00'), at('13:00'), court, 'x']), /not_found|forbidden/);
    await assert.rejects(propose(p, id, '12:00', '13:00', court, '  '), /bad_reason/);
    await propose(p, id, '12:00', '13:00', ring);
    const r = await req(id);
    assert.equal(r.status, 'proposed'); assert.equal(r.version, 1); assert.equal(String(r.proposed_amenity_id), String(ring));
    assert.ok(r.proposal_expires_at <= new Date(Date.now() + 24 * 36e5 + 5000));
  });
  await test('accepting the current proposal confirms the session and its reservation together', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00');
    await propose(p, id, '12:00', '13:00', ring);
    await assert.rejects(accept(p, id, 0), /stale_proposal/);
    await assert.rejects(as(p.coach, 'select accept_proposal($1,1)', [id]), /not_found|forbidden/);
    assert.equal(await accept(p, id, 1), 'confirmed');
    const r = await req(id);
    assert.equal(r.status, 'approved'); assert.equal(+r.requested_start, +new Date(at('12:00'))); assert.equal(String(r.amenity_id), String(ring));
    const rows = await booked(p);
    assert.deepEqual(rows.map(x => [String(x.amenity_id), x.coach_id, x.s, x.linked]), [[String(ring), p.coach, '12:00', true]]);
    await assert.rejects(accept(p, id, 1), /already_processed/);
  });
  await test('if the proposed time was taken meanwhile, nothing is booked and the proposal stays open', async () => {
    const p = await pair(), q = await pair(), id = await ask(p, '10:00', '11:00');
    await propose(p, id, '14:00', '15:00', court);
    const blocker = await ask(q, '14:30', '15:30', court);
    await as(q.coach, 'select respond_time_request($1,true)', [blocker]);
    await assert.rejects(accept(p, id, 1), /slot_taken/);
    assert.equal((await req(id)).status, 'proposed');
    assert.equal((await booked(p)).length, 0);
    assert.equal((await db.query('select count(*)::int n from training_sessions where member_id=$1', [p.member])).rows[0].n, 0);
  });
  await test('an expired proposal cannot be accepted; the request goes back to the coach', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00');
    await propose(p, id, '12:00', '13:00');
    await db.query(`update time_requests set proposal_expires_at=now()-interval '1 minute' where time_request_id=$1`, [id]);
    assert.equal(await accept(p, id, 1), 'expired');
    assert.equal((await req(id)).status, 'pending');
    await as(p.coach, 'select respond_time_request($1,true)', [id]);   // the coach can still approve the original
    assert.deepEqual((await booked(p)).map(x => x.s), ['10:00']);
  });
  await test('request another time goes back to the same coach as a pending request', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00');
    await propose(p, id, '12:00', '13:00');
    await as(p.member, 'select request_another_time($1,$2,$3,$4,null)', [id, at('16:00'), at('17:00'), ring]);
    const r = await req(id);
    assert.equal(r.status, 'pending'); assert.equal(r.version, 2); assert.equal(+r.requested_start, +new Date(at('16:00'))); assert.equal(String(r.amenity_id), String(ring));
    assert.equal(r.proposed_start, null);
    await as(p.coach, 'select respond_time_request($1,true)', [id]);
    assert.deepEqual((await booked(p)).map(x => [String(x.amenity_id), x.s]), [[String(ring), '16:00']]);
  });
  await test('declines need a reason, from the coach or from the member on a proposal', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00');
    await assert.rejects(as(p.coach, 'select decline_time_request($1,$2)', [id, '']), /bad_reason/);
    await as(p.coach, `select decline_time_request($1,'Fully booked that day')`, [id]);
    const r = await req(id); assert.equal(r.status, 'rejected'); assert.equal(r.decline_reason, 'Fully booked that day');
    const id2 = await ask(p, '10:00', '11:00');
    await propose(p, id2, '12:00', '13:00');
    await as(p.member, `select decline_time_request($1,'That does not suit me')`, [id2]);
    assert.equal((await req(id2)).status, 'rejected');
  });
  await test('an amenity session must sit inside the coach\'s posted shift', async () => {
    const p = await pair();
    const id = await as(p.member, 'select request_session($1,$2,$3,$4,null) id', [p.coach, at('10:00', other), at('11:00', other), court]).then(r => r[0].id);
    await assert.rejects(as(p.coach, 'select respond_time_request($1,true)', [id]), /coach_unavailable/);
    assert.equal((await req(id)).status, 'pending');
  });
  await test('a reschedule moves the session and its reservation together, or not at all', async () => {
    const p = await pair(), q = await pair(), id = await ask(p, '08:00', '09:00');
    await as(p.coach, 'select respond_time_request($1,true)', [id]);
    const session = (await db.query('select training_session_id from training_sessions where member_id=$1', [p.member])).rows[0].training_session_id;
    const blocker = await ask(q, '18:00', '19:00', court);
    await as(q.coach, 'select respond_time_request($1,true)', [blocker]);
    const bad = (await as(p.member, 'select request_reschedule($1,$2,$3) id', [session, at('18:30'), at('19:30')]))[0].id;
    await assert.rejects(as(p.coach, 'select respond_time_request($1,true)', [bad]), /slot_taken/);
    assert.deepEqual((await booked(p)).map(x => x.s), ['08:00']);
    await as(p.member, 'select cancel_time_request($1)', [bad]);
    const good = (await as(p.member, 'select request_reschedule($1,$2,$3) id', [session, at('13:00'), at('14:00')]))[0].id;
    await as(p.coach, 'select respond_time_request($1,true)', [good]);
    assert.deepEqual((await booked(p)).map(x => x.s), ['13:00']);
    const s = (await db.query(`select to_char(scheduled_start at time zone 'Asia/Manila','HH24:MI') s from training_sessions where training_session_id=$1`, [session])).rows[0].s;
    assert.equal(s, '13:00');
  });
  await test('cancelling the session frees its amenity', async () => {
    const p = await pair(), id = await ask(p, '20:00', '21:00');
    await as(p.coach, 'select respond_time_request($1,true)', [id]);
    const session = (await db.query('select training_session_id from training_sessions where member_id=$1', [p.member])).rows[0].training_session_id;
    await as(p.member, `select cancel_session($1,'Sick')`, [session]);
    assert.equal((await booked(p)).length, 0);
  });
  await test('extending a coach session moves the session and its reservation together', async () => {
    const p = await pair(), id = await ask(p, '19:00', '20:00', ring);   // a ring time no other test uses
    await as(p.coach, 'select respond_time_request($1,true)', [id]);
    const res = (await db.query('select reservation_id, training_session_id from reservations where member_id=$1 and status=\'confirmed\'', [p.member])).rows[0];
    await as(p.coach, 'select extend_reservation($1,30)', [res.reservation_id]);
    const both = (await db.query(`select to_char(r.ends_at at time zone 'Asia/Manila','HH24:MI') r, to_char(s.scheduled_end at time zone 'Asia/Manila','HH24:MI') s
      from reservations r join training_sessions s using (training_session_id) where r.reservation_id=$1`, [res.reservation_id])).rows[0];
    assert.deepEqual(both, { r: '20:30', s: '20:30' });
  });
  await test('D7: a session stops blocking new requests once its end time has passed', async () => {
    const p = await pair(), id = await ask(p, '10:00', '11:00', null);
    await as(p.coach, 'select respond_time_request($1,true)', [id]);
    await assert.rejects(ask(p, '12:00', '13:00', null), /active_coaching_session/);
    await db.query(`update training_sessions set scheduled_start=now()-interval '2 hours', scheduled_end=now()-interval '1 hour' where member_id=$1`, [p.member]);
    assert.ok(await ask(p, '12:00', '13:00', null));
  });
  await test('every change is in an append-only history only the two people (and staff) can read', async () => {
    const p = await pair(), stranger = await user(), id = await ask(p, '10:00', '11:00');
    await propose(p, id, '06:00', '07:00', ring);   // a ring time no other test uses
    await accept(p, id, 1);
    const events = await as(p.member, 'select actor_id, from_status, to_status from time_request_events where time_request_id=$1 order by event_id', [id]);
    assert.deepEqual(events.map(e => [e.actor_id, e.from_status, e.to_status]),
      [[p.member, null, 'pending'], [p.coach, 'pending', 'proposed'], [p.member, 'proposed', 'approved']]);
    assert.equal((await as(stranger, 'select * from time_request_events where time_request_id=$1', [id])).length, 0);
    await assert.rejects(as(p.member, 'delete from time_request_events where time_request_id=$1', [id]), /permission denied/);
    await assert.rejects(as(p.member, `update time_request_events set reason='x' where time_request_id=$1`, [id]), /permission denied/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nStructured requests: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
