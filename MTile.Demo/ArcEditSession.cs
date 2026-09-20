using MTile;

namespace MTileDemo;

// AN OPEN ARC EDIT inside the clip editor. Holds a WORKING COPY: nothing is written to the
// clip until Save, so cancelling a fork leaves no trace — which is what makes "edit an arc
// from a clip and the result belongs to that clip" safe to offer on any arc, shared ones
// included.
//
//   StoredLocal == null → this arc is not in the clip yet (a fork of a shared arc, or a new
//                         one). Save adds it to AnimationDocument.Arcs.
//   StoredLocal != null → editing a clip-local arc in place. Save copies the working values
//                         back into it.
//
// The shared ReferenceClips/ file is never written from here; "Promote to shared" is the one
// explicit way out, and it copies rather than links.
internal sealed class ArcEditSession
{
    public readonly HermiteClipDocument Working;      // edited in place by the canvas + panel
    public readonly HermiteClipDocument StoredLocal;  // the clip-local arc Save overwrites, or null
    public readonly SceneOverlay        Overlay;      // the row that opened this (may be null)
    public readonly string              ForkedFrom;   // shared arc it was forked from, else null

    public bool MapOnSave;                 // re-run Map com to arc after saving
    public int  Selected   = -1;           // key index shown in the panel
    public int  DragKey    = -1;
    public int  DragHandle = -1;
    public int  DragAnchor = -1;           // 0 = entry, 1 = gate
    public int  HandleSide = 1;            // +1 outgoing tip, −1 incoming
    public bool Dirty;

    public ArcEditSession(HermiteClipDocument working, HermiteClipDocument storedLocal,
                          SceneOverlay overlay, string forkedFrom)
    {
        Working = working;
        StoredLocal = storedLocal;
        Overlay = overlay;
        ForkedFrom = forkedFrom;
    }

    public bool Dragging => DragKey >= 0 || DragHandle >= 0 || DragAnchor >= 0;
    public void EndDrag() { DragKey = -1; DragHandle = -1; DragAnchor = -1; }
}
