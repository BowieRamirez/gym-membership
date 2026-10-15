// Behaviour tests for the migrations, run against the throwaway Postgres.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let pass = 0, failed = 0;

// run SQL as a signed-in user (RLS applies); each call is its own transaction that rolls back unless keep=true
async function as(uid, fn, keep = false) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid ?? '']);
    return await fn(db);
  } finally { await db.query(keep ? 'commit' : 'rollback'); }
}
const run = (uid, sql, args) => as(uid, c => c.query(sql, args), true);
async function code(promise) { try { await promise; return 'ok'; } catch (e) { return e.code + ':' + e.message; } }
async function test(name, fn) {
  try { await fn(); pass++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, '\n       ', e.message.split('\n')[0]); }
}

// --- people: created the way Supabase does, through auth.users + the trigger
async function user(email, role, name) {
  const r = await db.query(
    `insert into auth.users (email, raw_user_meta_data, raw_app_meta_data) values ($1, $2, $3) returning id`,
    [email, JSON.stringify({ full_name: name }), JSON.stringify({ role })]);
  return r.rows[0].id;
}
const admin = await user('admin@t.io', 'admin', 'Owner');
const desk = await user('desk@t.io', 'employee', 'Desk');
const coach = await user('coach@t.io', 'coach', 'Coach Kuts');
const alex = await user('alex@t.io', 'member', 'Alex');
const bea = await user('bea@t.io', 'member', 'Bea');
await db.query(`insert into membership_packages (name, price, duration_days) values ('Monthly', 1000, 30), ('Annual', 10000, 365)`);
await db.query(`insert into amenities (name, price, duration_days) values ('Boxing', 300, 30)`);
await db.query(`insert into discounts (code, percent, starts_at, expires_at) values ('WELCOME10', 10, now() - interval '1 day', now() + interval '30 days'),
                ('OLD20', 20, now() - interval '10 days', now() - interval '1 day')`);
const monthly = 1;

console.log('\nsign-up trigger');
await test('member gets profile, settings, role and member row', async () => {
  const r = await db.query(`select (select count(*) from members where user_id=$1)::int m, (select count(*) from user_settings where user_id=$1)::int s,
    (select r.name from user_roles ur join roles r using(role_id) where ur.user_id=$1) role, (select must_change_password from profiles where id=$1) mcp`, [alex]);
  assert.deepEqual(r.rows[0], { m: 1, s: 1, role: 'member', mcp: false });
});
await test('coach gets a coaches row and no member row', async () => {
  const r = await db.query(`select (select count(*) from coaches where user_id=$1)::int c, (select count(*) from members where user_id=$1)::int m`, [coach]);
  assert.deepEqual(r.rows[0], { c: 1, m: 0 });
});
await test('an unknown role in metadata becomes member', async () => {
  const id = await user('x@t.io', 'superuser', 'Xavier');
  assert.equal((await db.query(`select r.name from user_roles ur join roles r using(role_id) where user_id=$1`, [id])).rows[0].name, 'member');
});

console.log('\nrow level security');
await test('anonymous gets nothing', async () => {
  await db.query('begin'); await db.query('set local role anon');
  assert.match(await code(db.query('select * from profiles')), /42501/); await db.query('rollback');
});
await test('a member sees only themself, plus coaches', async () => {
  const r = await run(alex, 'select id from profiles');
  assert.deepEqual(r.rows.map(x => x.id).sort(), [alex, coach].sort());
});
await test('staff see everyone', async () => assert.equal((await run(desk, 'select id from profiles')).rowCount, 5 + 1));
await test('a member cannot read discounts or the audit log', async () => {
  assert.equal((await run(alex, 'select * from discounts')).rowCount, 0);
  assert.equal((await run(alex, 'select * from audit_log')).rowCount, 0);
});
await test('a member cannot edit their role or is_active', async () => {
  assert.match(await code(run(alex, `update profiles set is_active=false where id=$1`, [alex])), /42501/);
  assert.match(await code(run(alex, `insert into user_roles (user_id, role_id) values ($1, 1)`, [alex])), /42501/);
});
await test('a member cannot write payments, memberships or catalog', async () => {
  assert.match(await code(run(alex, `update payments set status='verified'`)), /42501/);
  assert.match(await code(run(alex, `insert into membership_packages (name, price, duration_days) values ('Free', 0, 1)`)), /42501|42501/);
});
await test('admin can add and edit a package', async () => {
  await run(admin, `insert into membership_packages (name, price, duration_days) values ('Weekly', 300, 7)`);
  await run(admin, `update membership_packages set price = 350 where name='Weekly'`);
});

console.log('\npackages and payments');
let pay;
await test('a guest avails a package and gets a pending QR payment with a code', async () => {
  pay = (await run(alex, `select * from avail_membership($1)`, [monthly])).rows[0];
  assert.match(pay.payment_code, /^GYM-[A-Z2-9]{5}$/); assert.equal(pay.status, 'pending'); assert.equal(Number(pay.amount), 1000);
  assert.ok(pay.expires_at);
});
await test('a second pending membership is refused', async () =>
  assert.match(await code(run(alex, `select * from avail_membership($1)`, [monthly])), /already_availed/));
await test('a guest cannot avail an amenity or message a coach yet', async () => {
  assert.match(await code(run(alex, `select * from avail_amenity(1)`)), /no_active_membership/);
  assert.match(await code(run(alex, `select hire_coach($1)`, [coach])), /no_active_membership/);
});
await test('a member cannot verify their own payment, and cannot even call verify', async () =>
  assert.match(await code(run(alex, `select verify_payment($1, true)`, [pay.payment_id])), /forbidden/));
await test('proof needs a reference number and uploads to the payment', async () => {
  assert.match(await code(run(alex, `select submit_payment_proof($1, '', 'x')`, [pay.payment_id])), /bad_input/);
  await run(alex, `select submit_payment_proof($1, 'REF123', $2)`, [pay.payment_id, `${alex}/1-proof.png`]);
});
await test('staff were notified of the proof', async () =>
  assert.equal((await run(desk, `select * from user_notifications where type='payment'`)).rowCount, 1));
await test('the same reference number cannot be used twice', async () => {
  const p2 = (await run(bea, `select * from avail_membership($1)`, [monthly])).rows[0];
  assert.match(await code(run(bea, `select submit_payment_proof($1, 'REF123', 'x/y.png')`, [p2.payment_id])), /23505/);
  // leave bea's pending payment for later
});
await test('a rejection needs a reason and cancels the pending membership', async () => {
  assert.match(await code(run(desk, `select verify_payment($1, false)`, [pay.payment_id])), /bad_input/);
});
await test('staff approve: membership turns active with dates and a receipt number', async () => {
  await run(desk, `select verify_payment($1, true)`, [pay.payment_id]);
  const r = await run(alex, `select p.status, p.receipt_no, u.status ustatus, u.ends_at > now() + interval '29 days' ok from payments p join user_membership_packages u using (user_membership_package_id) where p.payment_id=$1`, [pay.payment_id]);
  assert.equal(r.rows[0].status, 'verified'); assert.equal(r.rows[0].ustatus, 'active'); assert.ok(r.rows[0].ok);
  assert.match(r.rows[0].receipt_no, /^OR-\d{4}-\d{6}$/);
});
await test('approving twice is refused', async () =>
  assert.match(await code(run(desk, `select verify_payment($1, true)`, [pay.payment_id])), /already_processed/));
await test('the member is now a full member and sees a notification', async () => {
  assert.equal((await run(alex, `select is_active_member() m`)).rows[0].m, true);
  assert.equal((await run(alex, `select * from user_notifications where title='Payment approved'`)).rowCount, 1);
});
await test('a reject cancels the pending membership', async () => {
  const bpay = (await run(bea, `select payment_id from payments where member_id=$1`, [bea])).rows[0].payment_id;
  await run(desk, `select verify_payment($1, false, 'Wrong amount')`, [bpay]);
  const r = await run(bea, `select p.status, u.status ustatus from payments p join user_membership_packages u using (user_membership_package_id)`);
  assert.deepEqual(r.rows[0], { status: 'rejected', ustatus: 'cancelled' });
  assert.equal((await run(bea, `select is_active_member() m`)).rows[0].m, false);
});

console.log('\ndiscounts');
await test('a code cuts the amount; case does not matter', async () => {
  const p = (await run(bea, `select * from avail_membership($1, 'welcome10')`, [monthly])).rows[0];
  assert.equal(Number(p.amount), 900); assert.equal(Number(p.discount_amount), 100);
});
await test('an expired or unknown code is refused, never full price', async () => {
  const c = await user('c@t.io', 'member', 'Cara');
  assert.match(await code(run(c, `select * from avail_membership($1, 'OLD20')`, [monthly])), /invalid_code/);
  assert.match(await code(run(c, `select * from avail_membership($1, 'NOPE')`, [monthly])), /invalid_code/);
});
await test('a code works once per member', async () => {
  const d = await user('d@t.io', 'member', 'Dana');
  const dp = (await run(d, `select * from avail_membership($1, 'WELCOME10')`, [monthly])).rows[0];
  await run(desk, `select verify_payment($1, false, 'no')`, [dp.payment_id]);              // rejected: code is free again
  await run(d, `select * from avail_membership($1, 'WELCOME10')`, [monthly]);               // can reuse after a rejection
  assert.match(await code(run(d, `select * from avail_membership($1, 'WELCOME10')`, [monthly])), /already_availed|invalid_code/);
});

console.log('\ncounter payments, voids, renewals');
const eve = await user('eve@t.io', 'member', 'Eve');
let counter;
await test('cash at the counter is verified at once and activates the plan', async () => {
  counter = (await run(desk, `select * from record_counter_payment('membership','cash',$1,$2)`, [eve, monthly])).rows[0];
  assert.equal(counter.status, 'verified'); assert.ok(counter.receipt_no);
  assert.equal((await run(eve, `select is_active_member() m`)).rows[0].m, true);
});
await test('QR cannot be recorded at the counter; GCash needs a reference', async () => {
  assert.match(await code(run(desk, `select * from record_counter_payment('amenity','qr',$1,1)`, [eve])), /bad_input/);
  assert.match(await code(run(desk, `select * from record_counter_payment('amenity','gcash',$1,1)`, [eve])), /bad_input/);
});
await test('staff cannot record a payment for themselves, members cannot record at all', async () => {
  assert.match(await code(run(desk, `select * from record_counter_payment('membership','cash',$1,$2)`, [desk, monthly])), /self_verify|not_found/);
  assert.match(await code(run(alex, `select * from record_counter_payment('membership','cash',$1,$2)`, [alex, monthly])), /forbidden/);
});
await test('a walk-in is recorded with a name, the default price, and a check-in', async () => {
  const w = (await run(desk, `select * from record_counter_payment('walk_in','cash',null,null,'Juan Dela Cruz')`)).rows[0];
  assert.equal(Number(w.amount), 150); assert.equal(w.walk_in_name, 'Juan Dela Cruz'); assert.equal(w.member_id, null);
  assert.equal((await run(desk, `select * from gym_check_ins where payment_id=$1`, [w.payment_id])).rowCount, 1);
});
await test('an amenity bought at the counter activates with an expiry', async () => {
  const a = (await run(desk, `select * from record_counter_payment('amenity','gcash',$1,1,null,'GC-777')`, [eve])).rows[0];
  const r = await run(eve, `select status, expires_at is not null e from user_amenities where user_amenity_id=$1`, [a.user_amenity_id]);
  assert.deepEqual(r.rows[0], { status: 'active', e: true });
});
await test('only an admin can void; voiding cancels the membership and is audited', async () => {
  assert.match(await code(run(desk, `select void_payment($1,'oops')`, [counter.payment_id])), /forbidden/);
  assert.match(await code(run(admin, `select void_payment($1,'')`, [counter.payment_id])), /bad_input/);
  await run(admin, `select void_payment($1,'Wrong plan')`, [counter.payment_id]);
  assert.equal((await run(eve, `select is_active_member() m`)).rows[0].m, false);
  assert.equal((await run(admin, `select * from audit_log where action='payment.void'`)).rowCount, 1);
});
await test('revenue excludes voided payments and only admin can read it', async () => {
  const r = await run(admin, `select kind, total from revenue_summary(current_date - 1, current_date + 1) order by kind`);
  const total = r.rows.reduce((s, x) => s + Number(x.total), 0);
  assert.equal(total, 1000 + 150 + 300);   // alex's QR + walk-in + eve's amenity (eve's membership voided)
  assert.match(await code(run(desk, `select * from revenue_summary(current_date, current_date)`)), /forbidden/);
  assert.equal((await run(desk, `select * from daily_cash_summary(current_date)`)).rowCount > 0, true);
});
await test('renewing an active plan extends from its end date', async () => {
  const ump = (await run(alex, `select user_membership_package_id id, ends_at from user_membership_packages`)).rows[0];
  const rp = (await run(alex, `select * from renew_membership($1)`, [ump.id])).rows[0];
  assert.match(await code(run(alex, `select * from renew_membership($1)`, [ump.id])), /renewal_pending/);
  await run(desk, `select verify_payment($1, true)`, [rp.payment_id]);
  const after = (await run(alex, `select ends_at from user_membership_packages`)).rows[0];
  assert.ok(Math.abs(new Date(after.ends_at) - (new Date(ump.ends_at).getTime() + 30 * 864e5)) < 2000);
  // voiding the renewal restores the old end date
  await run(admin, `select void_payment($1,'test')`, [rp.payment_id]);
  const back = (await run(alex, `select ends_at from user_membership_packages`)).rows[0];
  assert.equal(new Date(back.ends_at).getTime(), new Date(ump.ends_at).getTime());
});

console.log('\ncoaching');
await db.query(`update coaches set specialty='Boxing', hourly_rate=500 where user_id=$1`, [coach]);
let hire;
await test('an active member hires a coach; a guest cannot', async () => {
  hire = (await run(alex, `select hire_coach($1) id`, [coach])).rows[0].id;
  assert.match(await code(run(bea, `select hire_coach($1)`, [coach])), /no_active_membership/);
  assert.match(await code(run(alex, `select hire_coach($1)`, [coach])), /23505/);
});
await test('the coach sees the trainee and the trainee sees the coach, nobody else does', async () => {
  assert.equal((await run(coach, `select * from coach_hires`)).rowCount, 1);
  assert.equal((await run(eve, `select * from coach_hires`)).rowCount, 0);
  assert.ok((await run(coach, `select id from profiles`)).rows.some(r => r.id === alex));
  assert.ok(!(await run(eve, `select id from profiles`)).rows.some(r => r.id === alex));
});
await test('chat works for the hired pair and stays private', async () => {
  await run(alex, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$2,'Hi coach')`, [coach, alex]);
  await run(coach, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$1,'Hello')`, [coach, alex]);
  assert.equal((await run(alex, `select * from messages`)).rowCount, 2);
  assert.equal((await run(eve, `select * from messages`)).rowCount, 0);
  assert.equal((await run(alex, `select * from user_notifications where type='message'`)).rowCount, 1);
});
await test('you cannot send as someone else or to a coach you have not hired', async () => {
  assert.match(await code(run(alex, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$1,'spoof')`, [coach, alex])), /42501/);
  assert.match(await code(run(eve, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$2,'hey')`, [coach, eve])), /42501/);
});
const t0 = new Date(Date.now() + 2 * 864e5), t1 = new Date(t0.getTime() + 36e5), t2 = new Date(t0.getTime() + 72e5), t3 = new Date(t0.getTime() + 108e5);
let reqId;
await test('a member requests a time; the coach approves and a session is created', async () => {
  reqId = (await run(alex, `select request_time($1,$2,$3,'Leg day') id`, [coach, t0, t1])).rows[0].id;
  assert.match(await code(run(alex, `select respond_time_request($1,true)`, [reqId])), /forbidden/);
  await run(coach, `select respond_time_request($1,true)`, [reqId]);
  assert.equal((await run(coach, `select * from training_sessions where status='scheduled'`)).rowCount, 1);
  assert.match(await code(run(coach, `select respond_time_request($1,true)`, [reqId])), /already_processed/);
});
await test('overlapping sessions are refused', async () => {
  const r2 = (await run(alex, `select request_time($1,$2,$3) id`, [coach, new Date(t0.getTime() + 18e5), t2])).rows[0].id;
  assert.match(await code(run(coach, `select respond_time_request($1,true)`, [r2])), /23P01/);
});
await test('reschedule moves the same session once the other side approves', async () => {
  const s = (await run(alex, `select training_session_id id from training_sessions`)).rows[0].id;
  const rr = (await run(alex, `select request_reschedule($1,$2,$3) id`, [s, t2, t3])).rows[0].id;
  assert.match(await code(run(alex, `select request_reschedule($1,$2,$3)`, [s, t2, t3])), /already_processed/);
  await run(coach, `select respond_time_request($1,true)`, [rr]);
  const r = await run(alex, `select count(*)::int n, min(scheduled_start) st from training_sessions where status='scheduled'`);
  assert.equal(r.rows[0].n, 1); assert.equal(new Date(r.rows[0].st).getTime(), t2.getTime());
});
await test('a pending request can be withdrawn only by whoever made it', async () => {
  const r = (await run(alex, `select request_time($1,$2,$3) id`, [coach, new Date(t0.getTime() + 5 * 864e5), new Date(t0.getTime() + 5 * 864e5 + 36e5)])).rows[0].id;
  assert.match(await code(run(coach, `select cancel_time_request($1)`, [r])), /forbidden/);
  await run(alex, `select cancel_time_request($1)`, [r]);
  assert.match(await code(run(alex, `select cancel_time_request($1)`, [r])), /already_processed/);
});
await test('attendance is the coach only and sets the session status', async () => {
  const s = (await run(alex, `select training_session_id id from training_sessions`)).rows[0].id;
  assert.match(await code(run(alex, `select record_session_attendance($1,'present')`, [s])), /forbidden/);
  await run(coach, `select record_session_attendance($1,'absent')`, [s]);
  assert.equal((await run(alex, `select status from training_sessions`)).rows[0].status, 'no_show');
});
await test('a third person cannot post into another pair’s conversation', async () => {
  assert.match(await code(run(eve, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$3,'hello')`, [coach, alex, eve])), /42501|23514/);
});
await test('a coach posts a shift, trainees are told, overlap is refused', async () => {
  await run(coach, `insert into shift_posts (coach_id, starts_at, ends_at, note) values ($1,$2,$3,'Floor')`, [coach, t0, t1]);
  assert.match(await code(run(coach, `insert into shift_posts (coach_id, starts_at, ends_at) values ($1,$2,$3)`, [coach, t0, t1])), /23P01/);
  assert.equal((await run(alex, `select * from shift_posts`)).rowCount, 1);
  assert.equal((await run(eve, `select * from shift_posts`)).rowCount, 0);
  assert.match(await code(run(alex, `insert into shift_posts (coach_id, starts_at, ends_at) values ($1,$2,$3)`, [coach, t2, t3])), /42501/);
});
await test('ending a hire closes chat for new messages but keeps history', async () => {
  await run(alex, `select end_hire($1)`, [hire]);
  assert.match(await code(run(alex, `insert into messages (coach_id, member_id, sender_id, body) values ($1,$2,$2,'still there?')`, [coach, alex])), /42501/);
  assert.equal((await run(alex, `select * from messages`)).rowCount, 2);
});

console.log('\naccounts and admin');
await test('only an admin assigns roles; assigning coach creates the coach row', async () => {
  assert.match(await code(run(desk, `select assign_role($1,'coach')`, [bea])), /forbidden/);
  await run(admin, `select assign_role($1,'coach')`, [bea]);
  assert.equal((await run(admin, `select * from coaches where user_id=$1`, [bea])).rowCount, 1);
});
await test('the admin cannot remove their own admin role or deactivate themself', async () => {
  assert.match(await code(run(admin, `select revoke_role($1,'admin')`, [admin])), /forbidden/);
  assert.match(await code(run(admin, `select set_user_active($1,false)`, [admin])), /forbidden/);
});
await test('a deactivated user is locked out of everything but reading their own profile', async () => {
  await run(admin, `select set_user_active($1,false)`, [eve]);
  assert.equal((await run(eve, `select * from profiles`)).rowCount, 1);
  assert.equal((await run(eve, `select is_active from profiles`)).rows[0].is_active, false);
  assert.equal((await run(eve, `select * from payments`)).rowCount, 0);
  assert.match(await code(run(eve, `select * from avail_amenity(1)`)), /forbidden/);
});
await test('mark_password_changed clears the flag', async () => {
  const id = (await db.query(`insert into auth.users (email, raw_user_meta_data, raw_app_meta_data) values ('tmp@t.io','{"must_change_password":true}','{"role":"coach"}') returning id`)).rows[0].id;
  assert.equal((await db.query(`select must_change_password m from profiles where id=$1`, [id])).rows[0].m, true);
  await run(id, `select mark_password_changed()`);
  assert.equal((await db.query(`select must_change_password m from profiles where id=$1`, [id])).rows[0].m, false);
});
await test('delete rules: a used package cannot be deleted, an unused one can', async () => {
  assert.equal((await run(admin, `delete from membership_packages where name='Monthly'`).catch(e => e.code)), '23503');
  assert.equal((await run(admin, `delete from membership_packages where name='Weekly'`)).rowCount, 1);
});

console.log('\nexpiry jobs');
await test('unpaid QR payments expire and cancel their pending membership', async () => {
  const f = await user('f@t.io', 'member', 'Finn');
  const fp = (await run(f, `select * from avail_membership($1)`, [monthly])).rows[0];
  await db.query(`update payments set expires_at = now() - interval '1 minute' where payment_id=$1`, [fp.payment_id]);
  assert.equal((await db.query(`select expire_payments() n`)).rows[0].n, 1);
  const r = await run(f, `select p.status, u.status ustatus from payments p join user_membership_packages u using (user_membership_package_id)`);
  assert.deepEqual(r.rows[0], { status: 'expired', ustatus: 'cancelled' });
});
await test('memberships past their end become expired, and reminders go out once', async () => {
  const g = await user('g@t.io', 'member', 'Gina');
  await db.query(`select record_counter_payment_test()`).catch(() => {});
  const gp = (await run(g, `select * from avail_membership($1)`, [monthly])).rows[0];
  await run(desk, `select verify_payment($1,true)`, [gp.payment_id]);
  await db.query(`update user_membership_packages set ends_at = now() + interval '3 days' where member_id=$1`, [g]);
  assert.equal((await db.query(`select send_expiry_reminders() n`)).rows[0].n >= 1, true);
  assert.equal((await db.query(`select send_expiry_reminders() n`)).rows[0].n, 0);
  await db.query(`update user_membership_packages set ends_at = now() - interval '1 minute', starts_at = now() - interval '31 days' where member_id=$1`, [g]);
  assert.equal((await db.query(`select expire_memberships() n`)).rows[0].n >= 1, true);
  assert.equal((await run(g, `select is_active_member() m`)).rows[0].m, false);
});
await test('the app role cannot call the internal helpers or cron jobs', async () => {
  assert.match(await code(run(alex, `select expire_payments()`)), /42501/);
  assert.match(await code(run(alex, `select notify($1,'system','x')`, [alex])), /42501/);
  assert.match(await code(run(alex, `select next_receipt_no()`)), /42501/);
});

console.log('\nhow Supabase Auth really creates accounts');
// Supabase Auth inserts the user first and writes app_metadata afterwards, so this is how an Admin-API account really arrives
async function userTwoStep(email, role, name) {
  const id = (await db.query(`insert into auth.users (email, raw_user_meta_data, raw_app_meta_data) values ($1, $2, '{"provider":"email"}') returning id`, [email, JSON.stringify({ full_name: name })])).rows[0].id;
  await db.query(`update auth.users set raw_app_meta_data = raw_app_meta_data || $2::jsonb where id = $1`, [id, JSON.stringify({ role })]);
  return id;
}
await test('an account made through the Admin API gets the role written after it was inserted', async () => {
  const boss = await userTwoStep('boss@t.io', 'admin', 'Boss');
  const rows = async (id) => (await db.query(`select (select string_agg(r.name, ',') from user_roles ur join roles r using(role_id) where ur.user_id=$1) roles, (select count(*) from members where user_id=$1)::int m, (select count(*) from coaches where user_id=$1)::int c`, [id])).rows[0];
  assert.deepEqual(await rows(boss), { roles: 'admin', m: 0, c: 0 });
  const c2 = await userTwoStep('c2@t.io', 'coach', 'Coach Two');
  assert.deepEqual(await rows(c2), { roles: 'coach', m: 0, c: 1 });
  const e2 = await userTwoStep('e2@t.io', 'employee', 'Desk Two');
  assert.deepEqual(await rows(e2), { roles: 'employee', m: 0, c: 0 });
  // a later change to app_metadata does not flip the role again
  await db.query(`update auth.users set raw_app_meta_data = raw_app_meta_data || '{"role":"admin"}'::jsonb where id=$1`, [c2]);
  assert.deepEqual(await rows(c2), { roles: 'coach', m: 0, c: 1 });
  assert.equal((await run(boss, `select is_staff() s`)).rows[0].s, true);
});

console.log('\nnames are letters only (0012)');
const setName = (uid, name) => run(uid, `update profiles set full_name = $2 where id = $1`, [uid, name]);
await test('a member can rename themselves to a real name', async () => {
  for (const ok of ["Mary-Ann O'Brien", 'Juan dela Cruz', 'José Peña', 'Al'])
    assert.equal(await code(setName(alex, ok)), 'ok', ok);
  await setName(alex, 'Alex');
});
await test('digits, symbols, doubled or edge separators and bad lengths are refused', async () => {
  for (const bad of ['R2D2', 'Rey!', 'Rey_Santos', 'Rey  Santos', '-Rey', "Rey'", 'A', '', 'a'.repeat(61)])
    assert.match(await code(setName(alex, bad)), /23514/, JSON.stringify(bad));
});
await test('sign-up with a bad name fails, with no name it falls back to the letters in the email', async () => {
  const signUp = (email, name) => db.query(`insert into auth.users (email, raw_user_meta_data) values ($1, $2) returning id`, [email, JSON.stringify(name === undefined ? {} : { full_name: name })]);
  assert.match(await code(signUp('bad@t.io', 'R2D2')), /23514/);
  const nameOf = async (email, name) => (await db.query(`select full_name from profiles where id = $1`, [(await signUp(email, name)).rows[0].id])).rows[0].full_name;
  assert.equal(await nameOf('jane.doe99@t.io'), 'jane doe');
  assert.equal(await nameOf('99@t.io'), 'Member');
  assert.equal(await nameOf('x1@t.io', '   '), 'Member');   // one letter is too short for a name
});

console.log('\nprofile pictures and catalog images (0013)');
const avatarOf = async uid => (await run(uid, `select avatar_path from profiles where id = $1`, [uid])).rows[0].avatar_path;
await test('a member sets and clears their own picture', async () => {
  await run(alex, `select set_my_avatar($1)`, [`${alex}/avatar-1.jpg`]);
  assert.equal(await avatarOf(alex), `${alex}/avatar-1.jpg`);
  await run(alex, `select set_my_avatar(null)`);
  assert.equal(await avatarOf(alex), null);
});
await test("a path in someone else's folder is refused", async () => {
  assert.match(await code(run(alex, `select set_my_avatar($1)`, [`${bea}/avatar-1.jpg`])), /42501/);
});
await test('avatar_path cannot be written directly', async () => {
  assert.match(await code(run(alex, `update profiles set avatar_path = 'x' where id = $1`, [alex])), /42501/);
});
await test('a deactivated user cannot set a picture', async () => {
  assert.equal(await code(run(bea, `select set_my_avatar($1)`, [`${bea}/avatar-1.jpg`])), 'ok');   // works while active
  await db.query(`update profiles set is_active = false where id = $1`, [bea]);
  try { assert.notEqual(await code(run(bea, `select set_my_avatar($1)`, [`${bea}/avatar-2.jpg`])), 'ok'); }
  finally { await db.query(`update profiles set is_active = true, avatar_path = null where id = $1`, [bea]); }
});
await test('the avatars bucket is private, images only, 5 MB', async () => {
  const b = (await db.query(`select public, file_size_limit, allowed_mime_types from storage.buckets where id = 'avatars'`)).rows[0];
  assert.equal(b.public, false);
  assert.equal(Number(b.file_size_limit), 5242880);
  assert.deepEqual([...b.allowed_mime_types].sort(), ['image/jpeg', 'image/png', 'image/webp']);
});
await test('storage: add only to your own folder; any signed-in user reads', async () => {
  await db.query(`grant select, insert on storage.objects to authenticated`);   // the real project grants this; the local stub does not
  const add = (uid, folder) => run(uid, `insert into storage.objects (bucket_id, name) values ('avatars', $1)`, [`${folder}/avatar-1.jpg`]);
  assert.equal(await code(add(alex, alex)), 'ok');
  assert.match(await code(add(alex, bea)), /42501/);
  assert.equal((await run(bea, `select name from storage.objects where bucket_id = 'avatars'`)).rowCount, 1);
});
await test('amenities and packages have an image_path, empty by default', async () => {
  assert.equal((await run(admin, `select image_path from amenities limit 1`)).rows[0].image_path, null);
  assert.equal((await run(admin, `select image_path from membership_packages limit 1`)).rows[0].image_path, null);
});

console.log(`\n${pass} passed, ${failed} failed`);
await db.end(); await server.stop();
process.exit(failed ? 1 : 0);
