from mythic_payloadtype_container.MythicCommandBase import *
import json


class PpidArguments(TaskArguments):

    def __init__(self, command_line):
        super().__init__(command_line)
        self.args = {
            "ppid": CommandParameter(name="Parent Process ID", type=ParameterType.Number, required=True, default_value=-1),
        }

    async def parse_arguments(self):
        if len(self.command_line) == 0:
            raise Exception("No PPID given on command line.")
        if self.command_line[0] == "{":
            self.load_args_from_json_string(self.command_line)
        else:
            try:
                self.add_arg("ppid", int(self.command_line))
            except:
                raise Exception("Invalid integer given to PPID: {}".format(self.command_line))


class PpidCommand(CommandBase):
    cmd = "ppid"
    needs_admin = False
    help_cmd = "ppid [pid]"
    description = "Change the parent process for post-ex jobs by the specified pid."
    version = 1
    is_exit = False
    is_file_browse = False
    is_process_list = False
    is_download_file = False
    is_upload_file = False
    is_remove_file = False
    author = "@djhohnstein"
    argument_class = PpidArguments
    attackmapping = ["T1055"]

    async def create_tasking(self, task: MythicTask) -> MythicTask:
        pid = task.args.get_arg("ppid")
        task.display_params = "{}".format(pid)
        return task

    async def process_response(self, response: AgentResponse):
        pass