// IO handling ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
import { initializeApp } from "firebase/app";
import { getFirestore, collection, doc, setDoc, getDoc, addDoc, updateDoc, onSnapshot, } from "firebase/firestore";
import {run_game} from "./game.js"

// Your web app's Firebase configuration
const firebaseConfig = {
  apiKey: "AIzaSyD6QdGyS8ZfuBGGK4n__iCjL-Sig02I4ko",
  authDomain: "rtcpvp-aa61c.firebaseapp.com",
  projectId: "rtcpvp-aa61c",
  storageBucket: "rtcpvp-aa61c.firebasestorage.app",
  messagingSenderId: "224703323248",
  appId: "1:224703323248:web:4482f0165bd5b481b4e8b5"
};

// Initialize Firebase
const app = initializeApp(firebaseConfig);
const firestore = getFirestore(app);

const servers = {
  iceServers: [
    {
      urls: ['stun:stun1.l.google.com:19302', 'stun:stun2.l.google.com:19302'],
    },
  ],
  iceCandidatePoolSize: 10,
};

const pc = new RTCPeerConnection(servers);
var pc_channel = null; 
var player_ind = null; 

// HTML elements
const callButton = document.getElementById('callButton');
const callInput = document.getElementById('callInput');
const answerButton = document.getElementById('answerButton');
const hangupButton = document.getElementById('hangupButton');
const startGameButton = document.getElementById('startGameButton');


// 2. Create an offer
callButton.onclick = async () => {
    pc_channel = pc.createDataChannel("sendChannel");
    pc_channel.onopen = function () {
        // startGameButton.disabled = false; 
        player_ind = 0; 
        run_game(player_ind, pc_channel); 
    };
    pc_channel.onclose = function () {console.log('pc_channel closed');};
    pc_channel.onmessage = function (event) {console.log("Got Data Channel Message:", event.data);};

    // Reference Firestore collections for signaling
    const callDoc = doc(collection(firestore, 'calls'));
    const offerCandidates = collection(callDoc, 'offerCandidates');
    const answerCandidates = collection(callDoc, 'answerCandidates');
  
    callInput.value = callDoc.id;
  
    // Get candidates for caller, save to db
    pc.onicecandidate = async (event) => {
      if (event.candidate) {
        await addDoc(offerCandidates, event.candidate.toJSON());
      }
    };

    // Create offer
    const offerDescription = await pc.createOffer();
    await pc.setLocalDescription(offerDescription);
  
    const offer = {
      sdp: offerDescription.sdp,
      type: offerDescription.type,
    };
  
    await setDoc(callDoc, { offer });

    onSnapshot(callDoc, (snapshot) => {
      const data = snapshot.data();
      if (!pc.currentRemoteDescription && data?.answer) {
        const answerDescription = new RTCSessionDescription(data.answer);
        pc.setRemoteDescription(answerDescription);
      }
    });

    onSnapshot(answerCandidates, (snapshot) => {
      snapshot.docChanges().forEach((change) => {
        if (change.type === 'added') {
          const candidate = new RTCIceCandidate(change.doc.data());
          pc.addIceCandidate(candidate);
        }
      });
    }); 
    answerButton.disabled = true; 
  };



function handleReceiveMessage(event) {
    console.log(event.data); 
}
  
// Handle status changes on the receiver's channel.
function handleReceiveChannelStatusChange(event) {
    if (pc_channel) {
        console.log("Receive channel's status has changed to " +
                    pc_channel.readyState);
    }
}

function receiveChannelCallback(event) {
    pc_channel = event.channel;
    pc_channel.onmessage = handleReceiveMessage;
    pc_channel.onopen = handleReceiveChannelStatusChange;
    pc_channel.onclose = handleReceiveChannelStatusChange;
    startGameButton.disabled = false; 
    player_ind = 1; 
    run_game(player_ind, pc_channel); 

}

// 3. Answer the call with the unique ID
answerButton.onclick = async () => {
    pc.ondatachannel = receiveChannelCallback;

    const callId = callInput.value;
    const callDoc = doc(firestore, 'calls', callId);
    const answerCandidates = collection(callDoc, 'answerCandidates');
    const offerCandidates = collection(callDoc, 'offerCandidates');
  
    pc.onicecandidate = async (event) => {
      if (event.candidate) {
        await addDoc(answerCandidates, event.candidate.toJSON());
      }
    };
  
    const callDocSnapshot = await getDoc(callDoc);
    const callData = callDocSnapshot.data();
  
    const offerDescription = callData.offer;
    await pc.setRemoteDescription(new RTCSessionDescription(offerDescription));
  
    const answerDescription = await pc.createAnswer();
    await pc.setLocalDescription(answerDescription);
  
    const answer = {
      type: answerDescription.type,
      sdp: answerDescription.sdp,
    };
  
    await updateDoc(callDoc, { answer });
  
    onSnapshot(offerCandidates, (snapshot) => {
      snapshot.docChanges().forEach((change) => {
        // console.log(change);
        if (change.type === 'added') {
          const data = change.doc.data();
          pc.addIceCandidate(new RTCIceCandidate(data));
        }
      });
    });
    callButton.disabled = true; 
  };