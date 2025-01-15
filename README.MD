✅When a leader is active it sends a heart beat within 50ms.
✅When a node receives an AppendEntries from another node, then first node remembers that other node is the current leader.
✅When a new node is initialized, it should be in follower state.
✅When a follower doesn't get a message for 300ms then it starts an election.
✅When the election time is reset, it is a random value between 150 and 300ms.
✅When a new election begins, the term is incremented by 1.
✅When a follower does get an AppendEntries message, it resets the election timer. (i.e. it doesn't start an election even after more than 300ms)
✅Given an election begins, when the candidate gets a majority of votes, it becomes a leader. (think of the easy case; can use two tests for single and multi-node clusters)
✅Given a candidate receives a majority of votes while waiting for unresponsive node, it still becomes a leader.
✅A follower that has not voted and is in an earlier term responds to a RequestForVoteRPC with yes. (the reply will be a separate RPC)
✅Given a candidate server that just became a candidate, it votes for itself.
✅Given a candidate, when it receives an AppendEntries message from a node with a later term, then candidate loses and becomes a follower.
✅Given a candidate, when it receives an AppendEntries message from a node with an equal term, then candidate loses and becomes a follower.
✅If a node receives a second request for vote for the same term, it should respond no. (again, separate RPC for response)
✅If a node receives a second request for vote for a future term, it should vote for that node.
✅Given a candidate, when an election timer expires inside of an election, a new election is started.
✅When a follower node receives an AppendEntries request, it sends a response.
✅Given a candidate receives an AppendEntries from a previous term, then rejects.
✅When a candidate wins an election, it immediately sends a heart beat.
❌(testing persistence to disk will be a later assignment)