import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import pg from 'pg';

let duplicatePreflightChecked = false;
const { server, client: db } = await start({ beforeMigration: async (c, file, sql) => {
  if (!file.startsWith('0021_')) return;
  const rawUser = async (email, role) => (await c.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Migration Test"}',$2) returning id`, [email, JSON.stringify({role})])).rows[0].id;
  const coach = await rawUser('request-migration-coach@test.io', 'coach');
  const member = await rawUser('request-migration-member@test.io', 'member');
  await c.query(`insert into time_requests(coach_id,member_id,requested_by,requested_start,requested_end)
    values($1,$2,'member',now()+interval '1 day',now()+interval '2 days'),
          ($1,$2,'member',now()+interval '3 days',now()+interval '4 days')`, [coach, member]);
  await c.query('begin');
  try { await assert.rejects(c.query(sql), /duplicate_pending_session_requests/); }
  finally { await c.query('rollback'); }
  assert.equal((await c.query('select count(*)::int n from time_requests where member_id=$1',[member])).rows[0].n, 2);
  await c.query('delete from time_requests where member_id=$1', [member]);
  duplicatePreflightChecked = true;
}});
let passed = 0, failed = 0, serial = 0;
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Request Test"}',$2) returning id`, [`request-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function as(uid, sql, args = [], c = db) {
  await c.query('begin');
  try {
    await c.query('set local role authenticated');
    await c.query(`select set_config('request.jwt.claim.sub',$1,true)`, [uid ?? '']);
    const r = await c.query(sql,args);
    await c.query('commit');
    return r.rows;
  } catch (e) { await c.query('rollback'); throw e; }
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ',name); }
  catch(e) { failed++; console.error('  FAIL',name,e.message); }
}
const startTime = new Date(Date.now()+15*864e5), endTime = new Date(startTime.getTime()+36e5);
const desk = await user('employee');
const pkg = (await db.query(`insert into membership_packages(name,price,duration_days) values('Request test',1000,365) returning membership_package_id`)).rows[0].membership_package_id;
async function fixture() {
  const coach = await user('coach'), otherCoach = await user('coach'), member = await user(), otherMember = await user();
  for (const uid of [member, otherMember]) {
    const pay = (await as(uid,'select * from extend_membership($1)',[pkg]))[0];
    await as(desk,'select verify_payment($1,true)',[pay.payment_id]);
    await as(uid,'select hire_coach($1)',[coach]);
  }
  await as(member,'select hire_coach($1)',[otherCoach]);
  return {coach,otherCoach,member,otherMember};
}
const request = async (member,coach,c=db) => (await as(member,'select request_time($1,$2,$3) id',[coach,startTime,endTime],c))[0].id;
const invite = async (coach,member,c=db) => (await as(coach,'select request_member_time($1,$2,$3) id',[member,startTime,endTime],c))[0].id;
try {
  await test('populated duplicate preflight aborts without changing existing requests',async()=>assert.ok(duplicatePreflightChecked));
  await test('one pending member request per coach; different coaches and members remain independent',async()=>{
    const f=await fixture();
    await request(f.member,f.coach);
    await assert.rejects(request(f.member,f.coach),e=>e.code==='23505'&&e.message.includes('time_requests_one_pending_member_per_coach'));
    await request(f.member,f.otherCoach);
    await request(f.otherMember,f.coach);
  });
  for(const decision of ['reject','cancel']) await test(`${decision} frees the pending slot`,async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    if(decision==='cancel') await as(f.member,'select cancel_time_request($1)',[id]);
    else await as(f.coach,'select respond_time_request($1,$2)',[id,decision==='approve']);
    await request(f.member,f.coach);
  });
  await test('concurrent submissions through separate connections create exactly one pending request',async()=>{
    const f=await fixture(), c1=new pg.Client(db.connectionParameters), c2=new pg.Client(db.connectionParameters);
    await c1.connect(); await c2.connect();
    try {
      const results=await Promise.allSettled([request(f.member,f.coach,c1),request(f.member,f.coach,c2)]);
      assert.equal(results.filter(r=>r.status==='fulfilled').length,1);
      assert.equal(results.find(r=>r.status==='rejected').reason.code,'23505');
      assert.equal((await as(f.member,`select count(*)::int n from time_requests where coach_id=$1 and requested_by='member' and status='pending'`,[f.coach]))[0].n,1);
    } finally { await c1.end(); await c2.end(); }
  });
  await test('coach invitation is separate from the member pending limit and not yet a session',async()=>{
    const f=await fixture();
    await request(f.member,f.coach);
    const id=await invite(f.coach,f.member);
    const row=(await as(f.member,'select * from time_requests where time_request_id=$1',[id]))[0];
    assert.equal(row.member_id,f.member); assert.equal(row.requested_by,'coach');
    assert.equal((await as(f.coach,'select * from training_sessions')).length,0);
  });
  await test('only the selected member and sender can read a targeted invitation',async()=>{
    const f=await fixture(), id=await invite(f.coach,f.member);
    for(const uid of [f.otherMember,f.otherCoach,desk]) assert.equal((await as(uid,'select * from time_requests where time_request_id=$1',[id])).length,0);
    assert.equal((await as(f.coach,'select * from time_requests where time_request_id=$1',[id])).length,1);
  });
  await test('only the selected member can accept; acceptance creates one session and cannot be repeated',async()=>{
    const f=await fixture(), id=await invite(f.coach,f.member);
    for(const uid of [f.coach,f.otherMember,f.otherCoach,desk]) await assert.rejects(as(uid,'select respond_time_request($1,true)',[id]),/forbidden/);
    await as(f.member,'select respond_time_request($1,true)',[id]);
    const sessions=await as(f.coach,'select * from training_sessions');
    assert.equal(sessions.length,1); assert.equal(sessions[0].member_id,f.member);
    await assert.rejects(as(f.member,'select respond_time_request($1,true)',[id]),/already_processed/);
  });
  await test('the selected member can reject without creating a session',async()=>{
    const f=await fixture(), id=await invite(f.coach,f.member);
    await as(f.member,'select respond_time_request($1,false)',[id]);
    assert.equal((await as(f.coach,'select * from training_sessions')).length,0);
  });
  await test('members and front desk cannot send coach invitations; unrelated/ended hires are refused',async()=>{
    const f=await fixture();
    for(const uid of [f.member,desk]) await assert.rejects(invite(uid,f.otherMember),/forbidden/);
    await assert.rejects(invite(f.otherCoach,f.otherMember),/no_active_hire/);
    const hire=(await as(f.member,'select * from coach_hires where coach_id=$1',[f.coach]))[0];
    await as(f.member,'select end_hire($1)',[hire.coach_hire_id]);
    await assert.rejects(invite(f.coach,f.member),/no_active_hire/);
  });
  await test('inviting concurrently with ending the hire leaves no pending invitation in either lock order',async()=>{
    for(const inviteFirst of [true,false]) {
      const f=await fixture();
      const hire=(await as(f.member,'select * from coach_hires where coach_id=$1',[f.coach]))[0].coach_hire_id;
      const c1=new pg.Client(db.connectionParameters),c2=new pg.Client(db.connectionParameters);
      await c1.connect(); await c2.connect();
      try {
        await c1.query('begin'); await c1.query('set local role authenticated');
        await c1.query(`select set_config('request.jwt.claim.sub',$1,true)`,[inviteFirst?f.coach:f.member]);
        if(inviteFirst) await c1.query('select request_member_time($1,$2,$3)',[f.member,startTime,endTime]);
        else await c1.query('select end_hire($1)',[hire]);
        const waiting=inviteFirst
          ? as(f.member,'select end_hire($1)',[hire],c2).then(()=>({ok:true}),error=>({error}))
          : invite(f.coach,f.member,c2).then(()=>({ok:true}),error=>({error}));
        let blocked=false;
        for(let i=0;i<100;i++) {
          const state=(await db.query('select wait_event_type from pg_stat_activity where pid=$1',[c2.processID])).rows[0];
          if(state?.wait_event_type==='Lock') { blocked=true; break; }
          await new Promise(resolve=>setTimeout(resolve,10));
        }
        await c1.query('commit');
        const result=await waiting;
        assert.equal(blocked,true,'the competing operation must wait for the active-hire lock');
        if(inviteFirst) assert.equal(result.ok,true);
        else assert.match(result.error.message,/no_active_hire/);
        assert.equal((await db.query(`select count(*)::int n from time_requests where coach_id=$1 and member_id=$2 and status='pending'`,[f.coach,f.member])).rows[0].n,0);
      } finally { await c1.query('rollback'); await c1.end(); await c2.end(); }
    }
  });
  await test('only the selected trainee receives the new invitation notification',async()=>{
    const f=await fixture();
    await invite(f.coach,f.member);
    const own=await as(f.member,`select * from user_notifications where title='Your coach invited you to a session'`);
    const other=await as(f.otherMember,`select * from user_notifications where title='Your coach invited you to a session'`);
    assert.equal(own.length,1); assert.equal(other.length,0);
  });
  await test('inactive or expired members cannot receive invitations; inactive coaches cannot send',async()=>{
    const f=await fixture();
    await db.query('update profiles set is_active=false where id=$1',[f.member]);
    await assert.rejects(invite(f.coach,f.member),/invite_member_inactive/);
    await db.query(`update membership_terms set starts_at=now()-interval '3 days',ends_at=now()-interval '2 days' where member_id=$1`,[f.otherMember]);
    await assert.rejects(invite(f.coach,f.otherMember),/invite_member_inactive/);
    await db.query('update profiles set is_active=false where id=$1',[f.coach]);
    await assert.rejects(invite(f.coach,f.otherMember),/forbidden/);
  });
  await test('an active booking blocks new requests but allows rescheduling',async()=>{
    const f=await fixture(), id=await invite(f.coach,f.member);
    await as(f.member,'select respond_time_request($1,true)',[id]);
    const s=(await as(f.member,'select * from training_sessions'))[0];
    await assert.rejects(request(f.member,f.coach),/active_coaching_session/);
    await as(f.member,'select request_reschedule($1,$2,$3)',[s.training_session_id,new Date(startTime.getTime()+864e5),new Date(endTime.getTime()+864e5)]);
  });
  await test('legacy open slots still work and can be claimed by only one member',async()=>{
    const f=await fixture(), id=(await as(f.coach,'select request_time($1,$2,$3) id',[f.coach,startTime,endTime]))[0].id;
    await as(f.otherMember,'select respond_time_request($1,true)',[id]);
    await assert.rejects(as(f.member,'select respond_time_request($1,true)',[id]),/already_processed/);
  });
  await test('direct inserts and anonymous invitation execution are denied',async()=>{
    const f=await fixture();
    await assert.rejects(as(f.member,`insert into time_requests(coach_id,member_id,requested_by,requested_start,requested_end) values($1,$2,'member',$3,$4)`,[f.coach,f.member,startTime,endTime]),e=>e.code==='42501');
    await db.query('begin');
    try { await db.query('set local role anon'); await assert.rejects(db.query('select request_member_time($1,$2,$3)',[f.member,startTime,endTime]),e=>e.code==='42501'); }
    finally { await db.query('rollback'); }
  });
  await test('accepted member request blocks both new directions but not other pairs',async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    await as(f.coach,'select respond_time_request($1,true)',[id]);
    await assert.rejects(request(f.member,f.coach),/active_coaching_session/);
    await assert.rejects(invite(f.coach,f.member),/active_coaching_session/);
    await request(f.member,f.otherCoach);
    await invite(f.coach,f.otherMember);
  });
  for(const state of ['cancelled','completed','no_show']) await test(`${state} releases the active booking guard`,async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    await as(f.coach,'select respond_time_request($1,true)',[id]);
    const s=(await as(f.member,'select * from training_sessions'))[0];
    if(state==='cancelled') await as(f.member,'select cancel_session($1)',[s.training_session_id]);
    else await as(f.coach,'select record_session_attendance($1,$2)',[s.training_session_id,state==='completed'?'present':'absent']);
    await invite(f.coach,f.member);
    await request(f.member,f.coach);
  });
  await test('an old targeted invitation and open slot cannot bypass an accepted booking',async()=>{
    const f=await fixture(), invitation=await invite(f.coach,f.member), id=await request(f.member,f.coach);
    const slot=(await as(f.coach,'select request_time($1,$2,$3) id',[f.coach,startTime,endTime]))[0].id;
    await as(f.coach,'select respond_time_request($1,true)',[id]);
    await assert.rejects(as(f.member,'select respond_time_request($1,true)',[invitation]),/active_coaching_session/);
    await assert.rejects(as(f.member,'select respond_time_request($1,true)',[slot]),/active_coaching_session/);
    await as(f.member,'select respond_time_request($1,false)',[invitation]);
    assert.equal((await as(f.member,'select * from training_sessions')).length,1);
  });
  await test('reschedule approval moves the existing session and retains the active guard',async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    await as(f.coach,'select respond_time_request($1,true)',[id]);
    const s=(await as(f.member,'select * from training_sessions'))[0];
    const moved=new Date(startTime.getTime()+864e5), movedEnd=new Date(endTime.getTime()+864e5);
    const r=(await as(f.coach,'select request_reschedule($1,$2,$3) id',[s.training_session_id,moved,movedEnd]))[0].id;
    await as(f.member,'select respond_time_request($1,true)',[r]);
    const rows=await as(f.member,'select * from training_sessions');
    assert.equal(rows.length,1); assert.equal(rows[0].training_session_id,s.training_session_id);
    assert.equal(rows[0].scheduled_start.getTime(),moved.getTime());
    await assert.rejects(invite(f.coach,f.member),/active_coaching_session/);
  });
  await test('a scheduled session still blocks requests after its date passes until explicitly resolved',async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    await as(f.coach,'select respond_time_request($1,true)',[id]);
    await db.query(`update training_sessions set scheduled_start=now()-interval '2 days',scheduled_end=now()-interval '1 day' where coach_id=$1`,[f.coach]);
    await assert.rejects(invite(f.coach,f.member),/active_coaching_session/);
  });
  await test('simultaneous approvals of different requests cannot create two bookings',async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    const invitation=(await as(f.coach,'select request_member_time($1,$2,$3) id',[f.member,new Date(startTime.getTime()+864e5),new Date(endTime.getTime()+864e5)]))[0].id;
    const c1=new pg.Client(db.connectionParameters),c2=new pg.Client(db.connectionParameters);
    await c1.connect(); await c2.connect();
    try {
      const results=await Promise.allSettled([as(f.coach,'select respond_time_request($1,true)',[id],c1),as(f.member,'select respond_time_request($1,true)',[invitation],c2)]);
      assert.equal(results.filter(r=>r.status==='fulfilled').length,1);
      assert.match(results.find(r=>r.status==='rejected').reason.message,/active_coaching_session/);
      assert.equal((await as(f.member,'select * from training_sessions')).length,1);
    } finally { await c1.end(); await c2.end(); }
  });
  await test('an invitation racing an approval waits and is refused after the booking commits',async()=>{
    const f=await fixture(), id=await request(f.member,f.coach);
    const c1=new pg.Client(db.connectionParameters),c2=new pg.Client(db.connectionParameters);
    await c1.connect(); await c2.connect();
    try {
      await c1.query('begin'); await c1.query('set local role authenticated');
      await c1.query(`select set_config('request.jwt.claim.sub',$1,true)`,[f.coach]);
      await c1.query('select respond_time_request($1,true)',[id]);
      const waiting=invite(f.coach,f.member,c2).then(()=>({ok:true}),error=>({error}));
      let blocked=false;
      for(let i=0;i<100;i++) {
        if((await db.query('select wait_event_type from pg_stat_activity where pid=$1',[c2.processID])).rows[0]?.wait_event_type==='Lock') {blocked=true;break;}
        await new Promise(resolve=>setTimeout(resolve,10));
      }
      await c1.query('commit');
      const result=await waiting;
      assert.ok(blocked); assert.match(result.error.message,/active_coaching_session/);
    } finally { await c1.query('rollback'); await c1.end(); await c2.end(); }
  });
  await test('the internal guard and direct booking writes are not callable by clients',async()=>{
    const f=await fixture();
    await assert.rejects(as(f.member,'select assert_no_active_coaching_session($1,$2)',[f.coach,f.member]),e=>e.code==='42501');
    await assert.rejects(as(f.coach,`insert into training_sessions(coach_id,member_id,title,scheduled_start,scheduled_end) values($1,$2,'Bypass',$3,$4)`,[f.coach,f.member,startTime,endTime]),e=>e.code==='42501');
  });
} finally { await db.end(); await server.stop(); }
console.log(`\nSession requests: ${passed} passed, ${failed} failed`);
if(failed) process.exitCode=1;
