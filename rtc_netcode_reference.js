import {Vector2, Box2} from "threejs-math"
import {build_player, build_player_inp, evil_AI, update, array_equal, input_equal} from "./game_state.js"
// import { pc_channel, player_ind } from "./rtc_connection.js";


// Input handling ////////////////////////////////////////////////////////////////////////////////////
var [x_dir, y_dir] = [0, 0];
var [ud, rd, dd, ld] = [0, 0, 0, 0]; 
var [space_d, shift_d] = [false, false]; 
var key_dict = {}; 

document.addEventListener("keydown", handledown);
document.addEventListener("keyup", handleup)

function handledown(e) {
    if(e.code == "ArrowDown" || e.code == "KeyS") {
        dd = 1;
    } else if(e.code == "ArrowUp" || e.code == "KeyW") {
        ud = 1;
    } else if(e.code == "ArrowLeft" || e.code == "KeyA") {
        ld = 1;
    } else if (e.code == "ArrowRight" || e.code == "KeyD") {
        rd = 1; 
    } else {
        key_dict[e.code] = true; 
    }
    if(e.code == "Space") {
        e.preventDefault(); 
    }
    [space_d, shift_d] = [key_dict['Space'], key_dict['LeftShift']]; 
    x_dir = rd - ld; y_dir = dd - ud; 
}
function handleup(e) {
    if(e.code == "ArrowDown" || e.code == "KeyS") {
        dd = 0;
        e.preventDefault();
    } else if(e.code == "ArrowUp" || e.code == "KeyW") {
        ud = 0;
    } else if(e.code == "ArrowLeft" || e.code == "KeyA") {
        ld = 0;
    } else if (e.code == "ArrowRight" || e.code == "KeyD") {
        rd = 0; 
    } else {
        key_dict[e.code] = false; 
    }
    [space_d, shift_d] = [key_dict['Space'], key_dict['LeftShift']]; 
    x_dir = rd - ld; y_dir = dd - ud; 
}

const canvas = document.getElementById("canvas");
canvas.oncontextmenu = function(e) { e.preventDefault(); e.stopPropagation(); }
const ctx = canvas.getContext("2d");

var mouse_x = 0; var mouse_y = 0; 
var mouse_left = false; var mouse_right = false; 

canvas.addEventListener('mousemove', (event) => {
  let rect = canvas.getBoundingClientRect();
  mouse_x = event.clientX - rect.left;
  mouse_y = event.clientY - rect.top;
});
  
// Function to update the mouse state
function updateMouseState(event) {
    mouse_left = (event.buttons & 1) === 1; // Left button
    mouse_right = (event.buttons & 2) === 2; // Right button
}

// Event listeners for mouse events
document.addEventListener("mousedown", updateMouseState);
document.addEventListener("mouseup", updateMouseState);
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

function render(gamestate) {
    if(gamestate.player_state[0].health <= 0) {
        ctx.font = "48px serif";
        ctx.fillStyle = 'red';
        ctx.fillText('P2 Wins!', 250, 250);
        return; 
    } else if(gamestate.player_state[1].health <= 0) {
        ctx.font = "48px serif";
        ctx.fillStyle = 'blue';
        ctx.fillText('P1 Wins!', 250, 250);
        return; 
    }

    ctx.fillStyle = "green";
    ctx.fillRect(0, 0, 1000, 1000);
    let players = gamestate.player_state; 
    for (var i = 0; i < players.length; i+=1) {
        var player = players[i];
        var x = player.pos.x; var y = player.pos.y; 
        ctx.fillStyle = (i === 0) ? "blue" : "red";
        ctx.fillRect(x, y, 10, 10);
        ctx.fillRect(x-1, y-12, 12*(player.health) / 100, 2)
    }

    for (var i = 0; i < gamestate.bullet_state.length; i+=1) {
        let bullet = gamestate.bullet_state[i]; 
        let x = bullet.pos.x; var y = bullet.pos.y; 
        ctx.fillStyle = "orange";
        ctx.fillRect(x, y, bullet.size, bullet.size);
    }
}

//global variables
let pt = build_player(); 
pt.pos = new Vector2(10, 10); 
let p1 = build_player(pt);
pt.pos = new Vector2(450, 450); 
let p2 = build_player(pt); 

let gamestate = {frame: 0, player_state: [p1, p2], bullet_state: []}; 

let buffer_start = 0; 
let buffer_len = 60; 
let input_frame_delay = 3; 

let gamestate_buffer = []; 
let local_input_buffer = []; //(frame, local_player_input)
let remote_input_buffer = []; //(frame, remote_player_input)

let default_inp = build_player_inp(); 
for (let i = 0; i < buffer_len; i+=1) {
    gamestate_buffer[i] = gamestate; 
    local_input_buffer[i] = [0, default_inp];
    remote_input_buffer[i] = [0, default_inp, false]; 
}

let remote_input_stack = []; 
function recieve_input(event) {
    let [remote_frame, remote_input] = JSON.parse(event.data); 
    if(remote_frame % 100 == 0) {
        // console.log("Got Data Channel Message:", event.data);
        console.log('stack size', remote_input_stack.length);
    }
    remote_input_stack.push([remote_frame, remote_input])
}

let highest_remote_frame = 0; 
function handle_input_stack() {
    let updated_stack = [];
    let curr_frame = gamestate.frame; 
    let rollback_frame = curr_frame; let rollback_flag = false; 
    for (let i = 0; i < remote_input_stack.length; i++) {
        let [remote_frame, remote_input] = remote_input_stack[i]; 
        highest_remote_frame = Math.max(remote_frame, highest_remote_frame); 
        if(remote_frame > curr_frame) { //No spot for frame in buffer, keep on stack. (remote is very far ahead)
            updated_stack.push([remote_frame, remote_input]); 
            // console.log('remote frame', remote_frame, 'curr frame', curr_frame); 
        } else if(remote_frame > curr_frame - buffer_len) { //
            let remote_frame_ind = (buffer_start - 1 - (curr_frame - remote_frame) + buffer_len) % buffer_len; 
            let [impute_frame, impute_input, impute_status] = remote_input_buffer[remote_frame_ind]; 
            if(impute_frame === remote_frame && !(input_equal(remote_input, impute_input))) {
                if(impute_status != true) console.log('double message detected'); 
                rollback_flag = true;
                rollback_frame = Math.min(rollback_frame, impute_frame); 
            }
            remote_input_buffer[remote_frame_ind] = [remote_frame, remote_input, false]; 
        }
    }
    remote_input_stack = updated_stack; 
    return [rollback_frame, rollback_flag]; 
}

function run_game(player_ind, datachannel) {
    const timestep = 1.0 / 30.0 * 1000;
    let previous_time = 0.0;
    let delta = 0.0;

    if(datachannel != null) {
        datachannel.onmessage = recieve_input; 
    }

    const loop = time => {
        const dt = time - previous_time;
        delta = delta + dt;

        // Update the previous time
        previous_time = time;
        // Update your game
        /* Preconditions: 
        gamestate=gamestate_buffer[(buffer_start-1)%buffer_len]
        */
        while (delta > timestep) {
            delta = delta - timestep;
            let curr_frame = gamestate.frame; 
            //Execute rollback
            let [rollback_frame, rollback_flag, highest_remote_frame] = handle_input_stack(); 
            if(rollback_flag) {
                console.log('running rollback from %d to %d', rollback_frame, curr_frame); 
                let rollback_frame_ind = (buffer_start - 1 - (curr_frame - rollback_frame) + buffer_len) % buffer_len; 
                let rollback_gamestate = gamestate; 
                while (rollback_frame <= curr_frame) {
                    let trailing_buffer_ind = (rollback_frame_ind - 1 - input_frame_delay + buffer_len) % buffer_len; 
                    let [local_frame, local_input] = local_input_buffer[trailing_buffer_ind]; 
                    let [remote_frame, remote_input, imputed] = remote_input_buffer[trailing_buffer_ind]; 
                    if(remote_frame != local_frame) console.log("you fucked up"); 
                    let input_state = player_ind == 0 ? [local_input, remote_input] : [remote_input, local_input]; 
                    rollback_gamestate = update(rollback_gamestate, input_state);
                    rollback_frame += 1;
                    rollback_frame_ind += 1; 
                }
            }

            if(curr_frame >= highest_remote_frame + input_frame_delay + 3) {
                continue; 
            }

            let trailing_buffer_ind = (buffer_start - 1 - input_frame_delay + buffer_len) % buffer_len; 
            let [local_frame, local_input] = local_input_buffer[trailing_buffer_ind]; 
            let [remote_frame, remote_input, imputed] = remote_input_buffer[trailing_buffer_ind]; 
            if(remote_frame != local_frame) { //Assume remote_input is unchanged from the previous step
                let [prf, pri, pi] = remote_input_buffer[(trailing_buffer_ind-1+buffer_len)%buffer_len]; 
                remote_input_buffer[trailing_buffer_ind] = [local_frame, pri, true]; 
                remote_input = pri; 
            }

            let input_state = player_ind == 0 ? [local_input, remote_input] : [remote_input, local_input]; 
            gamestate = update(gamestate, input_state);

            gamestate_buffer[buffer_start] = gamestate; 
            let curr_local_input = build_player_inp(x_dir, y_dir, mouse_x, mouse_y, mouse_left, mouse_right, (space_d || shift_d)); 
            local_input_buffer[buffer_start] = [gamestate.frame, curr_local_input]; 

            if(datachannel == null) {
                let curr_ai_input = evil_AI(gamestate); 
                remote_input_buffer[buffer_start] = [gamestate.frame, curr_ai_input, false]; 
            } else {
                //Send local input across datachannel
                let curr_input_data = JSON.stringify([gamestate.frame, curr_local_input]); 
                datachannel.send(curr_input_data); 
            }
            buffer_start = (buffer_start + 1) % buffer_len; //Update place in buffer
        }

        if(key_dict['KeyJ']) {
            console.log(gamestate); 
        }

        // Render your game
        render(gamestate);

        // Repeat
        window.requestAnimationFrame(loop);
    };

    // Launch
    window.requestAnimationFrame(time => {
    previous_time = time;

    window.requestAnimationFrame(loop);
    });
}

const startGameButton = document.getElementById('startGameButton');
startGameButton.onclick = (() => run_game(0, null)); 

export {run_game}; 