// Phase 4 (F9-F11, 2026-11-10): staff queues for members and coaches, announcements and the 7-day note, requests in the coach chat.
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
    values($1,$2,$3) returning id`, [`msg-${++serial}@test.io`, JSON.stringify({full_name: name}), JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const sysadmin = await user('admin', 'Sys Admin'), admin = await user('subadmin', 'Sub Admin'), desk = await user('employee', 'Desk Dana');
const coach = await user('coach', 'Coach Kai'), member = await user('member', 'Mia Member');
const send = (who, person, body, queue) => as(who, 'select send_support_message($1,$2,null,$3) id', [person, body, queue]);
const threads = who => as(who, 'select * from chat_threads()');
const read = (who, person, queue) => as(who, `select * from chat_messages('support',$1,null,null,50,$2)`, [person, queue]);
const alerts = async uid => (await db.query(`select title, reference from user_notifications where user_id=$1 order by notification_id`, [uid])).rows;

try {
  await test('a coach writes to the Admin queue: Admin and System admin see it, the front desk does not', async () => {
    await send(coach, coach, 'Can I change my shift pattern?', 'admin');
    for (const who of [admin, sysadmin]) {
      const t = (await threads(who)).find(x => x.kind === 'support' && x.member_id === coach && x.queue === 'admin');
      assert.ok(t, 'thread listed'); assert.equal(t.with_name, 'Coach Kai'); assert.equal(t.unread, 1);
      assert.equal((await read(who, coach, 'admin'))[0].body, 'Can I change my shift pattern?');
    }
    assert.ok(!(await threads(desk)).some(x => x.queue === 'admin'));
    await assert.rejects(read(desk, coach, 'admin'), /forbidden/);
    assert.deepEqual(await as(desk, `select * from support_messages where queue='admin'`), []);
    const own = (await threads(coach)).filter(x => x.kind === 'support').map(x => `${x.queue}:${x.with_name}`).sort();
    assert.deepEqual(own, ['admin:Admin', 'front_desk:Front desk']);   // the Front desk thread is always there
    assert.ok((await alerts(admin)).some(a => a.title === 'New message from Coach Kai to Admin'));
    assert.ok(!(await alerts(desk)).some(a => a.title.startsWith('New message from Coach Kai')));
  });
  await test('the System admin queue is for the System admin only; answers reach the person', async () => {
    await send(member, member, 'Refund question', 'system_admin');
    await assert.rejects(read(admin, member, 'system_admin'), /forbidden/);
    await assert.rejects(send(admin, member, 'Hi', 'system_admin'), /forbidden/);
    await assert.rejects(as(desk, `select claim_support_thread($1,true,'system_admin')`, [member]), /forbidden/);
    await as(sysadmin, `select claim_support_thread($1,true,'system_admin')`, [member]);
    await send(sysadmin, member, 'Looking into it', 'system_admin');
    const mine = await read(member, member, 'system_admin');
    assert.equal(mine[0].body, 'Looking into it'); assert.equal(mine[0].sender_name, null);   // the person never learns which staff member
    assert.ok((await alerts(member)).some(a => a.title === 'Message from System admin'));
    await as(member, `select mark_chat_read('support',$1,null,'system_admin')`, [member]);
    assert.equal((await threads(member)).find(x => x.queue === 'system_admin').unread, 0);
  });
  await test('the Front desk queue works as before for old calls without a queue', async () => {
    await as(member, `select send_support_message($1,'Locker key?')`, [member]);
    const t = (await threads(desk)).find(x => x.member_id === member && x.queue === 'front_desk');
    assert.ok(t); assert.equal(t.unread, 1);
    await as(desk, `select mark_chat_read('support',$1)`, [member]);
    assert.equal((await as(desk, `select * from chat_messages('support',$1)`, [member]))[0].body, 'Locker key?');
    await assert.rejects(as(member, `select send_support_message($1,'x',null,'announcements')`, [member]), /bad_input/);
    await assert.rejects(send(desk, desk, 'staff writing in', 'front_desk'), /forbidden/);   // only members and coaches write in
  });
  await test('photos sit in their queue folder; only that queue can read them', async () => {
    const path = `support/admin/${coach}/a.png`;
    assert.equal((await as(coach, 'select can_use_chat_file($1,true) ok', [path]))[0].ok, true);
    assert.equal((await as(admin, 'select can_use_chat_file($1,false) ok', [path]))[0].ok, true);
    assert.equal((await as(desk, 'select can_use_chat_file($1,false) ok', [path]))[0].ok, false);
    await assert.rejects(as(coach, `select send_support_message($1,'',$2,'admin')`, [coach, `support/${coach}/a.png`]), /forbidden/);   // front desk folder
  });

  await test('only Admin and System admin post announcements; members and coaches read them, read-only', async () => {
    for (const who of [desk, coach, member]) await assert.rejects(as(who, `select * from post_announcement('Closed Monday')`), /forbidden/);
    await assert.rejects(as(admin, `select * from post_announcement('  ')`), /bad_input/);
    await as(admin, `select * from post_announcement('Closed on Monday for repairs')`);
    await as(sysadmin, `select * from post_announcement('New yoga class','members')`);
    const mt = (await threads(member)).find(x => x.kind === 'announcements');
    assert.equal(mt.unread, 2); assert.equal(mt.can_send, false); assert.equal(mt.last_body, 'New yoga class');
    const ct = (await threads(coach)).find(x => x.kind === 'announcements');
    assert.equal(ct.unread, 1); assert.equal(ct.last_body, 'Closed on Monday for repairs');   // coaches don't get the members-only one
    assert.ok(!(await threads(desk)).some(x => x.kind === 'announcements'));
    assert.equal((await alerts(member)).filter(a => a.title === 'Announcement').length, 2);
    const lines = await as(member, `select * from chat_messages('announcements',$1)`, [member]);
    assert.deepEqual(lines.map(l => l.body), ['New yoga class', 'Closed on Monday for repairs']);
    await assert.rejects(as(member, `select * from chat_messages('announcements',$1)`, [coach]), /forbidden/);
    await as(member, `select mark_chat_read('announcements',$1)`, [member]);
    assert.equal((await threads(member)).find(x => x.kind === 'announcements').unread, 0);
    assert.equal((await db.query(`select count(*)::int n from user_notifications where user_id=$1 and reference ? 'announcement_id' and read_at is null`, [member])).rows[0].n, 0);
    const list = await as(admin, 'select * from announcements_list()');
    assert.equal(list[0].author_name, 'Sys Admin'); assert.equal(list[0].audience, 'members');
    await assert.rejects(as(desk, 'select * from announcements_list()'), /forbidden/);
    await assert.rejects(as(member, 'select * from announcements'), /permission denied/);
  });
  await test('the 7-day note is posted once, to that member only', async () => {
    const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('Msg Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
    const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    await db.query(`update membership_terms set ends_at = (date_trunc('day', now() at time zone 'Asia/Manila') + interval '7 days 12 hours')
      at time zone 'Asia/Manila' where member_id=$1`, [member]);
    await db.query('select send_expiry_reminders()'); await db.query('select send_expiry_reminders()');
    const notes = (await db.query(`select body from announcements where recipient_id=$1`, [member])).rows;
    assert.equal(notes.length, 1); assert.match(notes[0].body, /^Your membership ends in 1 week, on /);
    assert.equal((await alerts(member)).filter(a => a.title === 'Your membership ends in 1 week').length, 1);
    assert.ok((await as(member, `select * from chat_messages('announcements',$1)`, [member])).some(l => l.sender_name === 'For you'));
    assert.ok(!(await as(coach, `select * from chat_messages('announcements',$1)`, [coach])).some(l => l.sender_name === 'For you'));
  });

  // F11
  const desk2 = await user('employee');
  const pack2 = (await db.query(`insert into membership_packages(name,price,duration_days) values('Msg Two',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
  const pool = (await db.query(`insert into amenities(name,price,duration_days) values('Pool',300,30) returning amenity_id`)).rows[0].amenity_id;
  const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
  const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
  async function pair() {
    const c = await user('coach', 'Coach Lena'), m = await user('member', 'Ana Reyes');
    const pay = (await as(m, 'select * from extend_membership($1,null)', [pack2]))[0];
    await as(desk2, 'select verify_payment($1,true)', [pay.payment_id]);
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [c]);
    await as(m, 'select hire_coach($1)', [c]);
    await as(c, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [c, at('06:00'), at('22:00')]);
    return { c, m };
  }
  const chat = async p => (await db.query(`select sender_id, body, kind, time_request_id from messages where coach_id=$1 and member_id=$2 order by message_id`, [p.c, p.m])).rows;
  await test('a member request makes exactly one message in the chat, in the member\'s name, with the amenity and note', async () => {
    const p = await pair();
    const id = (await as(p.m, `select request_session($1,$2,$3,$4,'First session, please') id`, [p.c, at('09:00'), at('10:00'), pool]))[0].id;
    const msgs = await chat(p);
    assert.equal(msgs.length, 1);
    assert.equal(msgs[0].sender_id, p.m); assert.equal(msgs[0].kind, 'request'); assert.equal(String(msgs[0].time_request_id), String(id));
    assert.match(msgs[0].body, /^I would like to request a training session: \w{3} \d{1,2} \w{3}, 9:00 AM to 10:00 AM, at Pool\. Note: First session, please$/);
    assert.ok(!(await alerts(p.c)).some(a => a.title === 'New message'));   // the request sends its own alert, not a chat one
    const line = (await as(p.c, `select * from chat_messages('coach',$1,$2)`, [p.m, p.c]))[0];
    assert.equal(line.kind, 'request'); assert.equal(String(line.time_request_id), String(id));
  });
  await test('the coach\'s answers appear in the coach\'s name: propose, decline with the reason', async () => {
    const p = await pair();
    const id = (await as(p.m, `select request_session($1,$2,$3,null,null) id`, [p.c, at('09:00'), at('10:00')]))[0].id;
    await as(p.c, 'select propose_time($1,$2,$3,null,$4)', [id, at('11:00'), at('12:00'), 'Morning is full']);
    await as(p.m, 'select request_another_time($1,$2,$3,null,null)', [id, at('13:00'), at('14:00')]);
    await as(p.c, `select decline_time_request($1,'Away that day')`, [id]);
    const msgs = await chat(p);
    assert.deepEqual(msgs.map(m => m.sender_id), [p.m, p.c, p.m, p.c]);
    assert.match(msgs[1].body, /^Could we do another time\? .*11:00 AM to 12:00 PM\. Reason: Morning is full$/);
    assert.match(msgs[2].body, /^Could we do this time instead\? .*1:00 PM to 2:00 PM\.$/);
    assert.match(msgs[3].body, /^I declined the training session on .*\. Reason: Away that day$/);
  });
  await test('accepting and invitations post too; a request with an error posts nothing', async () => {
    const p = await pair();
    const id = (await as(p.m, `select request_session($1,$2,$3,null,null) id`, [p.c, at('15:00'), at('16:00')]))[0].id;
    await as(p.c, 'select respond_time_request($1,true)', [id]);
    let msgs = await chat(p);
    assert.equal(msgs.length, 2); assert.match(msgs[1].body, /^I accepted the training session: .*3:00 PM to 4:00 PM\.$/);
    await assert.rejects(as(p.m, `select request_session($1,$2,$3,null,null)`, [p.c, at('09:15'), at('10:00')]));   // refused: rolled back
    assert.equal((await chat(p)).length, 2);
    const q = await pair();
    await as(q.c, 'select request_member_time($1,$2,$3)', [q.m, at('17:00'), at('18:00')]);
    msgs = await chat(q);
    assert.equal(msgs.length, 1); assert.equal(msgs[0].sender_id, q.c); assert.match(msgs[0].body, /^I would like to invite you to a training session: .*5:00 PM to 6:00 PM\.$/);
    await assert.rejects(as(q.m, `insert into messages(coach_id,member_id,sender_id,body,kind) values($1,$2,$2,'fake','request')`, [q.c, q.m]), /permission denied/);
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nMessaging: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
