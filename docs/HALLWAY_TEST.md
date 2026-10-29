# Hallway test: WindFlow, 30 minutes, one architect

A task script for watching one architect who has never seen WindFlow use it for the first time. The goal
is to find where a new user gets stuck, not to teach the app. One participant, one observer, one session.

## Setup (before the participant arrives, 10 min)

- A Windows PC with the GPU free: close games and other GPU jobs, since they slow the solver and the
  timings then mean nothing. Note down anything that is still running.
- Build and start the app normally (`dotnet run -c Release --project src/WindFlow.App`). Leave it on the
  empty start screen in **Basic** mode. Delete or move `%LOCALAPPDATA%\WindFlow\settings.json` first so it
  opens with the defaults, and put it back afterwards.
- Have a stopwatch, this sheet and a way to record the screen and voice (with consent).
- Tell the participant: "We are testing the software, not you. Please think aloud. I can't help you
  during the tasks, but I'll answer everything at the end. Stop a task whenever you want."
- Give out each task on its own card. Read it out once, word for word. **Do not** name buttons, point at the
  screen or explain terms. If they ask, say "What would you try?" If they are still stuck after 3 minutes,
  note it as a failure, then show them just enough to go on to the next task.

## Tasks (about 25 min)

| # | Task, read out as written | Aim (observer only) | Time box |
|---|---|---|---|
| 1 | "Open the example house that comes with the program and get the air moving." | Sample, then Run | 3 min |
| 2 | "The wind comes from the south-west at about 5 m/s today. Set that up." | Wind direction 225, speed 5 | 3 min |
| 3 | "Which room gets the least fresh air? How sure are you?" | Aim (observer only): the fresh-air ACH and air-age columns answer "fresh air"; the total exchange (Expert line, counts air from other rooms) does not, so note whether they use it. Do they notice the Preview label, "~" and muted numbers? | 4 min |
| 4 | "Close the window you think matters most for that room and see what changes." | Opening switch or a click in the view; reading the change | 4 min |
| 5 | "Wait until the program says its numbers are final, then tell me the number for the living room." | Preview to averaged-pass strip, the bar, the ETA, the strip's status (Settled or Time limit reached), the numbers turning solid | 5 min |
| 6 | "You are happy with this result. Make sure nothing can change it by accident while you show it to a client." | Lock | 3 min |
| 7 | "Make a document of this result that you could email to the client." | Export PDF; does the cover say Preview or a fine-grid result, and settled or time-limited? | 3 min |
| 8 | "Now try the wind from the north instead." | Finding Unlock; seeing that the edit restarts Preview | 3 min |

## What the observer notes, per task

- **Time** from reading out the task to done, or to giving up.
- **Outcome**: done alone / done after a prompt / failed.
- **First click**: where they went first, and whether it was right.
- **Hesitations**: any pause over 10 s, and what they were looking at.
- **Words**: labels they read aloud, misread or asked about (for example "Preview", "Accurate", "ACH",
  "still air", "air age", "notes", "Lock"). Write down their own words for things.
- **Wrong beliefs**: anything they said about the result that the screen does not support (for example
  they read a Preview number as final, or thought Lock saves a file).
- **Tasks 5 and 6 only**: did they wait, or act while the bar was still running? Did they notice the
  numbers change look when the result became Accurate? After Lock, did they try to change the wind, and
  did the greyed-out control explain why?

## Closing questions (5 min)

1. In your own words, what is the difference between the first numbers you saw and the ones at the end?
2. Would you put the living room number from task 5 in a design report? Why, or why not?
3. What did the lock do, and when would you use it?
4. What was the most confusing moment, and what did you expect to happen there?
5. What would you need before using this on a real project?

## After the session

Write one line per problem: task number, what happened, how bad it was (blocked / slowed / cosmetic) and
the participant's own words. Fix the blockers first. Run the same script with the next participant before
changing the tasks, so the sessions can be compared.
