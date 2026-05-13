# ASAP Roadmap

## Milestone 1: Refine Patron Format Settings
**Status**: ⬜ In Progress

---

### Phase 1: Move eContent Messages to Format Rules
**Status**: ⬜ Not Started
**Objective**: Move eBook and eAudiobook message editors into the accordion for those formats and ensure they always display as messages.
**Depends on**: None

**Tasks**:
- [ ] Update `lib/format_rules.js` to include `message` field in rules and update default rules.
- [ ] Update `pb_public/staff/index.html` to remove the separate message textareas.
- [ ] Update `pb_public/staff/js/settings-ui.js` to render message editors inside the format rules accordion.
- [ ] Update `pb_public/staff/js/settings.js` to correctly serialize and populate the new format-specific messages.
- [x] Update `pb_public/patron/app.js` to use the messages from the format rules.
- [x] Polish UI labels to remove "(legacy)" terminology.

**Verification**:
- [ ] Staff can edit eBook/eAudiobook messages inside the accordion.
- [ ] eBook/eAudiobook formats in the patron portal display the configured messages.
- [ ] Field settings are hidden for eBook/eAudiobook formats in the staff UI.
- [ ] Custom format messages display correctly in the patron portal.
- [ ] Custom format messages save properly per library 
